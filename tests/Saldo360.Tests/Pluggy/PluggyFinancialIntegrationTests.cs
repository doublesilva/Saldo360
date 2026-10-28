using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Pluggy;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Pluggy;
using Saldo360.Domain.Transactions;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Pluggy;

/// <summary>
/// T09.3: contas Pluggy no cadastro financeiro e transações importadas alimentando o realizado. API real sobre PostgreSQL, cliente
/// Pluggy simulado (nenhum teste depende de contas reais).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Pluggy")]
[Trait("Dependency", "PostgreSQL")]
public sealed class PluggyFinancialIntegrationTests(PostgresFixture postgres) : PluggyApiTestBase(postgres)
{
    private static readonly DateOnly Today = BusinessCalendar.Today(DateTimeOffset.UtcNow);
    private static string D(int daysAgo) => Today.AddDays(-daysAgo).ToString("yyyy-MM-dd");

    private static PluggyTransaction Tx(string id, string account, int daysAgo, decimal amount, string status = "POSTED", string? operation = "PIX", string? description = null,
        PluggyCardMetadata? card = null, string? type = null, string? category = "Groceries") =>
        FakePluggyClient.Transaction(id, account, D(daysAgo), amount, status, card) with
        {
            OperationType = operation, Description = description ?? "Mercado", DescriptionRaw = description ?? "MERCADO", Type = type ?? (amount < 0 ? "DEBIT" : "CREDIT"), Category = category,
        };

    private sealed record Ctx(string Session, Guid Workspace, HttpClient Client, Guid Connection);

    private async Task<Ctx> ArrangeAsync(string user, bool seed = true)
    {
        var session = await SignInAsync(user);
        var workspace = await CreateWorkspaceAsync(session);
        var client = LabClient();
        var connection = await LinkAsync(client, session, workspace.Id, ItemA, "Itaú PF");
        Pluggy.TransactionPageSize = 3;
        if (seed)
            Pluggy.TransactionsByAccount[BankA] =
            [
                Tx("b1", BankA, 9, -10.25m), Tx("b2", BankA, 8, 99.9m, description: "SALARIO", category: "Salary"), Tx("b3", BankA, 7, -1m, "PENDING"),
                Tx("b4", BankA, 6, -20m, operation: "TRANSFERENCIA_MESMA_INSTITUICAO", description: "PARA POUPANCA", category: "Transfers"), Tx("b5", BankA, 5, -7m),
            ];
        return new(session, workspace.Id, client, connection);
    }

    private static string Integration(Guid workspace) => $"/api/financial-workspaces/{workspace}/pluggy-integration";

    private Task<HttpResponseMessage> PostAsync(Ctx c, string url, object? body = null) => Send(c.Client, HttpMethod.Post, url, c.Session, body ?? new { });

    private Task<HttpResponseMessage> GetAsync(Ctx c, string url) => Send(c.Client, HttpMethod.Get, url, c.Session);

    private async Task<JsonElement> JsonGetAsync(Ctx c, string url)
    {
        var response = await GetAsync(c, url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonOf(response);
    }

    private async Task<HttpResponseMessage> SyncAsync(Ctx c)
    {
        var response = await PostAsync(c, $"{Root(c.Workspace)}/connections/{c.Connection}/sync");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var run = await JsonOf(response);
        Assert.True(run.GetProperty("errors").GetInt32() == 0, run.GetProperty("errorSummary").GetString()); // a integração financeira também não pode falhar em silêncio
        return response;
    }

    private async Task<JsonElement> LinkBankAsync(Ctx c, string pluggyAccount = BankA, string name = "Itaú corrente", object? extra = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["connectionId"] = c.Connection, ["pluggyAccountId"] = pluggyAccount, ["mode"] = "CreateNew", ["name"] = name, ["financialScope"] = "PF",
        };
        if (extra is not null) foreach (var p in extra.GetType().GetProperties()) body[p.Name] = p.GetValue(extra);
        var response = await PostAsync(c, $"{Integration(c.Workspace)}/links", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await JsonOf(response);
    }

    private async Task<JsonElement> LinkCardAsync(Ctx c, int closingDay, int dueDay, string name = "Cartão Itaú")
    {
        var response = await PostAsync(c, $"{Integration(c.Workspace)}/links", new
        {
            connectionId = c.Connection, pluggyAccountId = CardA, mode = "CreateNew", name, closingDay, dueDay,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await JsonOf(response);
    }

    private async Task<Guid> CreateCategoryAsync(Ctx c, string name, string type)
    {
        var response = await Send(c.Client, HttpMethod.Post, $"/api/financial-workspaces/{c.Workspace}/categories", c.Session, new { name, type });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonOf(response)).GetProperty("id").GetGuid();
    }

    private async Task<List<FinancialTransaction>> CoreAsync(Guid workspace, bool includeCancelled = false)
    {
        await using var db = Db();
        return await db.FinancialTransactions.AsNoTracking().Where(t => t.WorkspaceId == workspace && (includeCancelled || !t.IsCancelled)).OrderBy(t => t.TransactionDate).ThenBy(t => t.Amount).ToListAsync();
    }

    private async Task<JsonElement> ReviewAsync(Ctx c) => await JsonGetAsync(c, $"{Integration(c.Workspace)}/review");

    private static string[] Reasons(JsonElement review) => review.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("reason").GetString()!).Order().ToArray();

    // ---- 2. e 1. Cadastro de contas ---------------------------------------------------------------------------------------

    [Fact]
    public async Task ManualAccountsAreUnchangedAndMarkedManual()
    {
        var c = await ArrangeAsync("t93-manual");

        var created = await PostAsync(c, $"/api/financial-workspaces/{c.Workspace}/accounts", new { name = "Carteira", type = "Cash", financialScope = "PF" });
        var json = await JsonOf(created);

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("Manual", json.GetProperty("origin").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("integration").ValueKind);
        await using var db = Db();
        Assert.Equal(0, await db.PluggyAccountLinks.CountAsync());
    }

    [Fact]
    public async Task AvailableAccountsAreListedWithMaskedNumbersAndSuggestionsOnly()
    {
        var c = await ArrangeAsync("t93-available");

        var response = await GetAsync(c, $"{Integration(c.Workspace)}/connections/{c.Connection}/accounts");
        var text = await response.Content.ReadAsStringAsync();
        var accounts = JsonDocument.Parse(text).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("12345-6789", text);
        Assert.DoesNotContain("5555444433332222", text);
        var bank = accounts.EnumerateArray().Single(a => a.GetProperty("pluggyAccountId").GetString() == BankA);
        var card = accounts.EnumerateArray().Single(a => a.GetProperty("pluggyAccountId").GetString() == CardA);
        Assert.Equal(("Bank", "Checking", true, "••••6789"), (bank.GetProperty("kind").GetString(), bank.GetProperty("suggestedAccountType").GetString(), bank.GetProperty("linkable").GetBoolean(), bank.GetProperty("maskedNumber").GetString()));
        Assert.Equal(("Card", 10, 17, "5000.00"), (card.GetProperty("kind").GetString(), card.GetProperty("card").GetProperty("closingDay").GetInt32(), card.GetProperty("card").GetProperty("dueDay").GetInt32(),
            card.GetProperty("card").GetProperty("creditLimit").GetString()));
    }

    [Fact]
    public async Task LinkingABankAccountCreatesAnIntegratedAccountWithAPersistentLink()
    {
        var c = await ArrangeAsync("t93-link");

        var detail = await LinkBankAsync(c);

        var link = detail.GetProperty("link");
        Assert.Equal(("Pluggy", "Bank", "Active"), (link.GetProperty("origin").GetString(), link.GetProperty("kind").GetString(), link.GetProperty("status").GetString()));
        Assert.Equal("1500.75", link.GetProperty("observed").GetProperty("balance").GetString());
        Assert.Equal("500.00", link.GetProperty("observed").GetProperty("overdraftContractedLimit").GetString());
        var accountId = link.GetProperty("financialAccountId").GetGuid();
        await using var db = Db();
        var persisted = await db.PluggyAccountLinks.AsNoTracking().SingleAsync();
        Assert.Equal((c.Workspace, c.Connection, BankA, accountId), (persisted.WorkspaceId, persisted.ConnectionId, persisted.PluggyAccountId, persisted.FinancialAccountId!.Value));
        var account = await db.FinancialAccounts.AsNoTracking().SingleAsync(a => a.Id == accountId);
        Assert.Equal(("Itaú corrente", "Itaú", null), (account.Name, account.Institution, account.OpeningBalance)); // saldo inicial nunca é preenchido sozinho

        var list = await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/accounts");
        var integrated = list.EnumerateArray().Single();
        Assert.Equal("Pluggy", integrated.GetProperty("origin").GetString());
        Assert.Equal("1500.75", integrated.GetProperty("integration").GetProperty("observedBalance").GetString());
        Assert.Equal(JsonValueKind.Null, integrated.GetProperty("openingBalance").ValueKind);
    }

    [Fact]
    public async Task LinkedAccountsAreMarkedUnavailableForANewLink()
    {
        var c = await ArrangeAsync("t93-marked");
        await LinkBankAsync(c);

        var accounts = await JsonGetAsync(c, $"{Integration(c.Workspace)}/connections/{c.Connection}/accounts");

        var bank = accounts.EnumerateArray().Single(a => a.GetProperty("pluggyAccountId").GetString() == BankA);
        Assert.False(bank.GetProperty("linkable").GetBoolean());
        Assert.Equal("AlreadyLinked", bank.GetProperty("notLinkableReason").GetString());
    }

    // ---- 3./4./5. Identificador inválido, vínculo duplicado -----------------------------------------------------------------

    [Fact]
    public async Task InvalidMalformedForeignOrUnknownAccountIdsAreRejected()
    {
        var c = await ArrangeAsync("t93-invalid");
        string Url() => $"{Integration(c.Workspace)}/links";

        foreach (var id in new[] { "nao-existe", "", BankB, "'; DROP TABLE x;--" }) // BankB é de outro Item
        {
            var response = await PostAsync(c, Url(), new { connectionId = c.Connection, pluggyAccountId = id, mode = "CreateNew", name = "X", financialScope = "PF" });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(c, Url(), new { connectionId = c.Connection, pluggyAccountId = BankA, mode = "Outro", name = "X", financialScope = "PF" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(c, Url(), new { connectionId = c.Connection, pluggyAccountId = BankA, mode = "CreateNew", name = "X", financialScope = "XX" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(c, Url(), new { connectionId = c.Connection, pluggyAccountId = BankA, mode = "CreateNew", name = "X", financialScope = "PF", workspaceId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(c, Url(), new { connectionId = Guid.NewGuid(), pluggyAccountId = BankA, mode = "CreateNew", name = "X", financialScope = "PF" })).StatusCode);
        await using var db = Db();
        Assert.Equal(0, await db.PluggyAccountLinks.CountAsync());
        Assert.Equal(0, await db.FinancialAccounts.CountAsync());
    }

    [Fact]
    public async Task AnExternalAccountOrAnInternalAccountCannotBeLinkedTwice()
    {
        var c = await ArrangeAsync("t93-duplicate");
        var first = await LinkBankAsync(c);
        var accountId = first.GetProperty("link").GetProperty("financialAccountId").GetGuid();

        var again = await PostAsync(c, $"{Integration(c.Workspace)}/links", new { connectionId = c.Connection, pluggyAccountId = BankA, mode = "CreateNew", name = "Outra", financialScope = "PF" });
        var existing = await PostAsync(c, $"{Integration(c.Workspace)}/links", new
        {
            connectionId = c.Connection, pluggyAccountId = CardA, mode = "LinkExisting", existingCardId = accountId, importFrom = D(30),
        });
        // Conta bancária do Saldo360 já integrada a outra conta externa.
        Pluggy.AccountsByItem[ItemA].Add(FakePluggyClient.Account("a0000000-0000-4000-8000-000000000009", ItemA));
        var sameTarget = await PostAsync(c, $"{Integration(c.Workspace)}/links", new
        {
            connectionId = c.Connection, pluggyAccountId = "a0000000-0000-4000-8000-000000000009", mode = "LinkExisting", existingAccountId = accountId, importFrom = D(30),
        });

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, existing.StatusCode); // cartão inexistente
        Assert.Equal(HttpStatusCode.Conflict, sameTarget.StatusCode);
        await using var db = Db();
        Assert.Equal(1, await db.PluggyAccountLinks.CountAsync());
        Assert.Equal(1, await db.FinancialAccounts.CountAsync());
    }

    [Fact]
    public async Task LinkingAnExistingAccountRequiresAStartDateAndIgnoresOlderHistory()
    {
        var c = await ArrangeAsync("t93-existing");
        var created = await Send(c.Client, HttpMethod.Post, $"/api/financial-workspaces/{c.Workspace}/accounts", c.Session, new { name = "Conta antiga", type = "Checking", financialScope = "PF" });
        var accountId = (await JsonOf(created)).GetProperty("id").GetGuid();
        var body = (string? importFrom) => new Dictionary<string, object?>
        {
            ["connectionId"] = c.Connection, ["pluggyAccountId"] = BankA, ["mode"] = "LinkExisting", ["existingAccountId"] = accountId, ["importFrom"] = importFrom,
        };

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(c, $"{Integration(c.Workspace)}/links", body(null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(c, $"{Integration(c.Workspace)}/links", body(Today.AddDays(2).ToString("yyyy-MM-dd")))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(c, $"{Integration(c.Workspace)}/links", body(D(8)))).StatusCode);
        await SyncAsync(c);

        // b1 (9 dias atrás) é anterior ao início; b2 (8), b4 (em revisão) e b5 entram.
        var core = await CoreAsync(c.Workspace);
        Assert.Equal(new[] { 99.9m, 7m }, core.Select(t => t.Amount).OrderByDescending(a => a));
        Assert.All(core, t => Assert.Equal(accountId, t.AccountId));
    }

    // ---- 6. Isolamento por workspace ----------------------------------------------------------------------------------------

    [Fact]
    public async Task OtherWorkspacesCannotSeeUseOrReviewTheLinks()
    {
        var c = await ArrangeAsync("t93-owner");
        var detail = await LinkBankAsync(c);
        var linkId = detail.GetProperty("link").GetProperty("id").GetGuid();
        await SyncAsync(c);
        var outsiderSession = await SignInAsync("t93-outsider");
        var outsiderWorkspace = await CreateWorkspaceAsync(outsiderSession);
        var outsider = new Ctx(outsiderSession, outsiderWorkspace.Id, c.Client, c.Connection);

        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(c with { Session = outsiderSession }, $"{Integration(c.Workspace)}/links")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(outsider, $"{Integration(outsiderWorkspace.Id)}/links/{linkId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(outsider, $"{Integration(outsiderWorkspace.Id)}/connections/{c.Connection}/accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(outsider, $"{Integration(outsiderWorkspace.Id)}/links", new { connectionId = c.Connection, pluggyAccountId = CardA, mode = "CreateNew", name = "X", closingDay = 5, dueDay = 12 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(c with { Session = outsiderSession }, $"{Integration(c.Workspace)}/review")).StatusCode);
        Assert.Empty((await JsonGetAsync(outsider, $"{Integration(outsiderWorkspace.Id)}/links")).EnumerateArray());
        var review = await ReviewAsync(c);
        var posting = review.GetProperty("items")[0].GetProperty("postingId").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(outsider, $"{Integration(outsiderWorkspace.Id)}/review/{posting}/resolve", new { resolution = "Ignore" })).StatusCode);
        Assert.Empty(await CoreAsync(outsiderWorkspace.Id));
    }

    [Fact]
    public async Task EndpointsRequireSessionCsrfAndTheEnabledIntegration()
    {
        var c = await ArrangeAsync("t93-auth");
        var url = $"{Integration(c.Workspace)}/links";

        Assert.Equal(HttpStatusCode.Unauthorized, (await c.Client.GetAsync(url)).StatusCode);
        using var noCsrf = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(new { }) };
        noCsrf.Headers.Add("Cookie", c.Session);
        Assert.NotEqual(HttpStatusCode.Created, (await c.Client.SendAsync(noCsrf)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(c, $"{Integration(c.Workspace)}/links/{Guid.NewGuid()}/opening-balance-suggestion", new { confirm = false })).StatusCode);

        var disabled = Factory.WithWebHostBuilder(b => b.UseSetting("Pluggy:Enabled", "false")).CreateClient(new() { BaseAddress = new("https://localhost"), HandleCookies = false });
        Assert.Equal(HttpStatusCode.NotFound, (await Send(disabled, HttpMethod.Get, url, c.Session)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(disabled, HttpMethod.Get, $"{Integration(c.Workspace)}/review", c.Session)).StatusCode);
    }

    // ---- 8./9. Transações bancárias POSTED e PENDING --------------------------------------------------------------------------

    [Fact]
    public async Task OnlyPostedBankMovementsFeedTheRealizedAndTransfersWithoutCounterpartWaitForReview()
    {
        var c = await ArrangeAsync("t93-posted");
        await CreateCategoryAsync(c, "Alimentação", "Expense");
        var salary = await CreateCategoryAsync(c, "Salário", "Income");
        var accountId = (await LinkBankAsync(c)).GetProperty("link").GetProperty("financialAccountId").GetGuid();

        await SyncAsync(c);

        var core = await CoreAsync(c.Workspace);
        Assert.Equal(3, core.Count);
        Assert.All(core, t => Assert.Equal((FinancialTransactionOrigin.PluggyImport, accountId, "BRL"), (t.Origin, t.AccountId, t.Currency)));
        Assert.Equal(new[] { (CategoryType.Expense, 10.25m), (CategoryType.Income, 99.90m), (CategoryType.Expense, 7m) }.OrderBy(x => x.Item2), core.Select(t => (t.Type, t.Amount)).OrderBy(x => x.Amount));
        Assert.Equal(salary, core.Single(t => t.Type == CategoryType.Income).CategoryId); // "Salary" → "Salário" por apelido conhecido
        Assert.Equal(2, core.Count(t => t.Type == CategoryType.Expense && t.CategoryId != null)); // "Groceries" → "Alimentação"
        Assert.DoesNotContain(core, t => t.Amount is 1m or 20m); // PENDING e transferência sem contrapartida ficam fora
        var review = await ReviewAsync(c);
        Assert.Equal(["TransferWithoutCounterpart"], Reasons(review));
        await using var db = Db();
        Assert.Equal(1, await db.PluggyPostings.CountAsync(p => p.State == PluggyPostingState.WaitingPosted));
    }

    [Fact]
    public async Task ImportedTransactionKeepsSourceDataUntouchedAndIsIdentifiedInTheMovementList()
    {
        var c = await ArrangeAsync("t93-identified");
        await LinkBankAsync(c);
        await SyncAsync(c);

        var page = await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/transactions?pageSize=100");

        Assert.All(page.GetProperty("items").EnumerateArray(), i => Assert.Equal("PluggyImport", i.GetProperty("origin").GetString()));
        await using var db = Db();
        var source = await db.PluggyImportedTransactions.AsNoTracking().SingleAsync(t => t.PluggyTransactionId == "b1");
        Assert.Equal((-10.25m, "MERCADO", PluggyTransactionStatus.Posted), (source.Amount, source.DescriptionRaw, source.Status));
        Assert.Equal(5, await db.PluggyImportedTransactions.CountAsync());
    }

    [Fact]
    public async Task RepeatedSyncsAndReprocessingNeverDuplicateMovements()
    {
        var c = await ArrangeAsync("t93-idempotent");
        await LinkBankAsync(c);
        await SyncAsync(c);
        var first = (await CoreAsync(c.Workspace)).Select(t => t.Id).Order().ToArray();

        await SyncAsync(c);
        var reprocess = await PostAsync(c, $"{Integration(c.Workspace)}/reprocess");
        await PostAsync(c, $"{Integration(c.Workspace)}/reprocess");

        Assert.Equal(HttpStatusCode.OK, reprocess.StatusCode);
        Assert.Equal(first, (await CoreAsync(c.Workspace, includeCancelled: true)).Select(t => t.Id).Order());
        await using var db = Db();
        Assert.Equal(5, await db.PluggyPostings.CountAsync());
        var summary = await JsonOf(await PostAsync(c, $"{Integration(c.Workspace)}/reprocess"));
        Assert.Equal(0, summary.GetProperty("created").GetInt32());
    }

    [Fact]
    public async Task PendingBecomesPostedAndSourceChangesFlowToTheMovementWithoutTouchingTheCategory()
    {
        var c = await ArrangeAsync("t93-update");
        await CreateCategoryAsync(c, "Mercado", "Expense");
        var chosen = await CreateCategoryAsync(c, "Lazer", "Expense");
        await LinkBankAsync(c);
        await SyncAsync(c);
        var b1 = (await CoreAsync(c.Workspace)).Single(t => t.Amount == 10.25m);
        var recategorized = await Send(c.Client, HttpMethod.Put, $"/api/financial-workspaces/{c.Workspace}/transactions/{b1.Id}/category", c.Session, new { categoryId = chosen });
        Assert.Equal(HttpStatusCode.OK, recategorized.StatusCode);

        Pluggy.TransactionsByAccount[BankA] =
        [
            Tx("b1", BankA, 9, -10.75m, description: "MERCADO CENTRAL"), Tx("b2", BankA, 8, 99.9m, description: "SALARIO"), Tx("b3", BankA, 7, -1m, "POSTED"),
            Tx("b4", BankA, 6, -20m, operation: "TRANSFERENCIA_MESMA_INSTITUICAO", description: "PARA POUPANCA"), Tx("b5", BankA, 5, -7m),
        ];
        await SyncAsync(c);

        var core = await CoreAsync(c.Workspace);
        Assert.Equal(4, core.Count);
        var updated = core.Single(t => t.Id == b1.Id);
        Assert.Equal((10.75m, "MERCADO CENTRAL", chosen), (updated.Amount, updated.Description, updated.CategoryId)); // valor/descrição seguem o banco; a categoria é do usuário
        Assert.Contains(core, t => t.Amount == 1m && t.Type == CategoryType.Expense); // PENDING → POSTED entra agora
        Assert.Single(core, t => t.Amount == 10.75m);
    }

    // ---- 14. Categoria alterada manualmente ----------------------------------------------------------------------------------

    [Fact]
    public async Task OnlyTheCategoryOfAnImportedMovementCanBeCorrectedAndTheChoiceIsValidated()
    {
        var c = await ArrangeAsync("t93-category");
        var expense = await CreateCategoryAsync(c, "Lazer", "Expense");
        var income = await CreateCategoryAsync(c, "Salário extra", "Income");
        await LinkBankAsync(c);
        await SyncAsync(c);
        var b1 = (await CoreAsync(c.Workspace)).Single(t => t.Amount == 10.25m);
        var url = $"/api/financial-workspaces/{c.Workspace}/transactions/{b1.Id}";

        var full = await Send(c.Client, HttpMethod.Put, url, c.Session, new { accountId = b1.AccountId, categoryId = expense, type = "Expense", description = "Outra", amount = 1m, transactionDate = D(1) });
        var wrongType = await Send(c.Client, HttpMethod.Put, $"{url}/category", c.Session, new { categoryId = income });
        var unknown = await Send(c.Client, HttpMethod.Put, $"{url}/category", c.Session, new { categoryId = Guid.NewGuid() });
        var extra = await Send(c.Client, HttpMethod.Put, $"{url}/category", c.Session, new { categoryId = expense, amount = 1m });
        var ok = await Send(c.Client, HttpMethod.Put, $"{url}/category", c.Session, new { categoryId = expense });

        Assert.Equal(HttpStatusCode.Conflict, full.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, wrongType.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, extra.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var after = (await CoreAsync(c.Workspace)).Single(t => t.Id == b1.Id);
        Assert.Equal((expense, 10.25m, "Mercado"), (after.CategoryId, after.Amount, after.Description));
        await using var db = Db();
        var posting = await db.PluggyPostings.AsNoTracking().SingleAsync(p => p.FinancialTransactionId == b1.Id);
        Assert.Equal((PluggyCategorySource.User, expense), (posting.CategorySource, posting.CategoryId));

        // Sincronizar de novo não desfaz a escolha; nulo remove a categoria.
        await SyncAsync(c);
        Assert.Equal(expense, (await CoreAsync(c.Workspace)).Single(t => t.Id == b1.Id).CategoryId);
        Assert.Equal(HttpStatusCode.OK, (await Send(c.Client, HttpMethod.Put, $"{url}/category", c.Session, new { categoryId = (Guid?)null })).StatusCode);
        Assert.Null((await CoreAsync(c.Workspace)).Single(t => t.Id == b1.Id).CategoryId);
    }

    [Fact]
    public async Task ManualTransactionsCannotUseTheImportedCategoryEndpoint()
    {
        var c = await ArrangeAsync("t93-manual-category");
        var account = (await JsonOf(await Send(c.Client, HttpMethod.Post, $"/api/financial-workspaces/{c.Workspace}/accounts", c.Session, new { name = "Conta", type = "Checking", financialScope = "PF" }))).GetProperty("id").GetGuid();
        var category = await CreateCategoryAsync(c, "Lazer", "Expense");
        var created = await Send(c.Client, HttpMethod.Post, $"/api/financial-workspaces/{c.Workspace}/transactions", c.Session,
            new { accountId = account, categoryId = category, type = "Expense", description = "Manual", amount = 5m, transactionDate = D(1) });
        var id = (await JsonOf(created)).GetProperty("id").GetGuid();

        var response = await Send(c.Client, HttpMethod.Put, $"/api/financial-workspaces/{c.Workspace}/transactions/{id}/category", c.Session, new { categoryId = category });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task AnImportedMovementCancelledByTheUserIsNotRecreated()
    {
        var c = await ArrangeAsync("t93-cancel");
        await LinkBankAsync(c);
        await SyncAsync(c);
        var b5 = (await CoreAsync(c.Workspace)).Single(t => t.Amount == 7m);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(c, $"/api/financial-workspaces/{c.Workspace}/transactions/{b5.Id}/cancel")).StatusCode);

        await SyncAsync(c);

        Assert.DoesNotContain(await CoreAsync(c.Workspace), t => t.Amount == 7m);
        Assert.Single(await CoreAsync(c.Workspace, includeCancelled: true), t => t.Amount == 7m);
        await using var db = Db();
        Assert.Equal(PluggyPostingState.Ignored, (await db.PluggyPostings.AsNoTracking().SingleAsync(p => p.ImportedTransactionId == db.PluggyImportedTransactions.Single(t => t.PluggyTransactionId == "b5").Id)).State);
    }

    // ---- 18. Precisão monetária ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task CentsAreExactAndAmountsBeyondCentsNeverEnterTheRealized()
    {
        var c = await ArrangeAsync("t93-precision", seed: false);
        Pluggy.TransactionsByAccount[BankA] = [Tx("p1", BankA, 3, 1234567.89m), Tx("p2", BankA, 3, -0.10m), Tx("p3", BankA, 3, -0.20m), Tx("p4", BankA, 3, -10.255m)];
        await LinkBankAsync(c);

        await SyncAsync(c);

        var core = await CoreAsync(c.Workspace);
        Assert.Equal(new[] { 0.10m, 0.20m, 1234567.89m }, core.Select(t => t.Amount).Order());
        Assert.Equal(0.30m, core.Where(t => t.Type == CategoryType.Expense).Sum(t => t.Amount));
        Assert.Equal(["AmountPrecision"], Reasons(await ReviewAsync(c)));
    }

    // ---- 10./11. Cartões, parcelas e pagamento de fatura ------------------------------------------------------------------------

    /// <summary>Fatura do banco: fecha 15 dias atrás e vence 8 dias atrás (o dia de fechamento do cartão criado nos testes acompanha estas datas).</summary>
    private void SeedBill(decimal total) =>
        Pluggy.BillsByAccount[CardA] =
        [
            new("bill-1", DateTimeOffset.Parse(D(8) + "T00:00:00Z"), total, "BRL", null, null, [], DateTimeOffset.Parse(D(15) + "T00:00:00Z"), []),
        ];

    private void SeedCard(params PluggyTransaction[] extra)
    {
        SeedBill(230m);
        Pluggy.TransactionsByAccount[CardA] =
        [
            Tx("c1", CardA, 20, 150m, operation: "PAGAMENTO", description: "LOJA", card: new(null, null, null, null, "bill-1", "2222", null, null), type: "DEBIT", category: null),
            Tx("c2", CardA, 19, 80m, operation: "PAGAMENTO", description: "TV", card: new(2, 10, null, null, "bill-1", "2222", null, null), type: "DEBIT", category: null),
            Tx("c0", CardA, 18, 30m, "PENDING", "PAGAMENTO", "AGUARDANDO", type: "DEBIT", category: null),
            ..extra,
        ];
    }

    [Fact]
    public async Task CardPurchasesAndInstallmentsBecomeInvoiceObligationsAndNeverBankExpenses()
    {
        var c = await ArrangeAsync("t93-card", seed: false);
        Pluggy.TransactionsByAccount[BankA] = [];
        SeedCard();
        var link = await LinkCardAsync(c, Today.AddDays(-15).Day, Today.AddDays(-8).Day);
        Assert.Equal("Card", link.GetProperty("link").GetProperty("kind").GetString());
        var cardId = link.GetProperty("link").GetProperty("creditCardId").GetGuid();

        await SyncAsync(c);

        Assert.Empty(await CoreAsync(c.Workspace)); // compra de cartão não vira lançamento bancário
        await using var db = Db();
        var card = await db.FinancialCreditCards.AsNoTracking().SingleAsync(x => x.Id == cardId);
        Assert.Equal((5000m, "2222", "Itaú"), (card.CreditLimit, card.LastFourDigits, card.Issuer));
        var purchases = await db.FinancialCreditCardPurchases.AsNoTracking().Include(p => p.Installments).Where(p => p.CreditCardId == cardId).OrderBy(p => p.PurchaseDate).ToListAsync();
        Assert.Equal(new[] { 150m, 80m }, purchases.Select(p => p.TotalAmount));
        Assert.Equal(["LOJA", "TV (2/10)"], purchases.Select(p => p.Description));
        Assert.All(purchases, p => Assert.Equal(1, p.InstallmentCount));
        var category = await db.FinancialCategories.AsNoTracking().SingleAsync(x => x.Id == purchases[0].CategoryId);
        Assert.Equal(("Outros", CategoryType.Expense), (category.Name, category.Type));
        Assert.All(purchases.SelectMany(p => p.Installments), i => Assert.NotNull(i.InvoiceId));
        var invoice = await db.FinancialCreditCardInvoices.AsNoTracking().SingleAsync();
        Assert.Equal(230m, await db.FinancialCreditCardInstallments.AsNoTracking().Where(i => i.InvoiceId == invoice.Id).SumAsync(i => i.Amount));
        Assert.Equal(1, await db.PluggyPostings.CountAsync(p => p.State == PluggyPostingState.WaitingPosted)); // o pendente aguarda

        await SyncAsync(c);
        Assert.Equal(2, await db.FinancialCreditCardPurchases.CountAsync(p => p.CreditCardId == cardId));
        var limit = await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/credit-cards/{cardId}/limit");
        Assert.Equal("230.00", limit.GetProperty("usedLimit").GetString());
    }

    [Fact]
    public async Task CardBillPaymentIsPairedWithTheBankDebitWithoutCountingConsumptionTwice()
    {
        var c = await ArrangeAsync("t93-bill", seed: false);
        Pluggy.TransactionsByAccount[BankA] = [Tx("pay-bank", BankA, 10, -230m, operation: "PIX", description: "PAGAMENTO DE FATURA", category: null), Tx("other", BankA, 4, -12m)];
        SeedCard(Tx("pay-card", CardA, 10, 230m, operation: "PAGAMENTO_FATURA", description: "PAGAMENTO FATURA", type: "CREDIT", category: null));
        var bankLink = await LinkBankAsync(c);
        var cardLink = await LinkCardAsync(c, Today.AddDays(-15).Day, Today.AddDays(-8).Day);
        var accountId = bankLink.GetProperty("link").GetProperty("financialAccountId").GetGuid();
        var cardId = cardLink.GetProperty("link").GetProperty("creditCardId").GetGuid();

        await SyncAsync(c);

        var core = await CoreAsync(c.Workspace);
        Assert.Equal(2, core.Count);
        var payment = core.Single(t => t.Amount == 230m);
        Assert.Equal((FinancialTransactionOrigin.CreditCardInvoicePayment, CategoryType.Expense, accountId), (payment.Origin, payment.Type, payment.AccountId));
        await using var db = Db();
        var invoicePayment = await db.FinancialCreditCardInvoicePayments.AsNoTracking().SingleAsync();
        Assert.Equal((payment.Id, 230m, FinancialCreditCardInvoicePaymentStatus.Active), (invoicePayment.TransactionId, invoicePayment.Amount, invoicePayment.Status));
        var invoices = await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/credit-cards/{cardId}/invoices?from={Today.AddMonths(-3):yyyy-MM}&through={Today.AddMonths(3):yyyy-MM}");
        var invoice = invoices.EnumerateArray().Single();
        Assert.Equal(("230.00", "230.00", "0.00", "Paid"), (invoice.GetProperty("totalAmount").GetString(), invoice.GetProperty("paidAmount").GetString(), invoice.GetProperty("remainingAmount").GetString(),
            invoice.GetProperty("paymentStatus").GetString()));
        var linkId = cardLink.GetProperty("link").GetProperty("id").GetGuid();
        var bills = (await JsonGetAsync(c, $"{Integration(c.Workspace)}/links/{linkId}")).GetProperty("reconciliation").GetProperty("card").GetProperty("bills").EnumerateArray().ToList();
        var bill = Assert.Single(bills);
        Assert.Equal(("Official", "230.00", "230.00", "0.00"), (bill.GetProperty("status").GetString(), bill.GetProperty("bankTotal").GetString(), bill.GetProperty("saldo360Total").GetString(), bill.GetProperty("difference").GetString()));
        var postings = await db.PluggyPostings.AsNoTracking().ToListAsync();
        Assert.Equal(1, postings.Count(p => p.State == PluggyPostingState.Covered));
        Assert.Equal(PluggyPostingKind.CardBillPayment, postings.Single(p => p.State == PluggyPostingState.Applied && p.Kind == PluggyPostingKind.CardBillPayment).Kind);

        // Consumo: só o pagamento (caixa) aparece no realizado bancário; as compras (230) ficam no cartão, não somam outra vez.
        var calendar = await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/financial-calendar?from={D(40)}&through={D(0)}");
        Assert.Equal("242.00", calendar.GetProperty("summary").GetProperty("realizedExpense").GetString());

        await SyncAsync(c);
        Assert.Equal(1, await db.FinancialCreditCardInvoicePayments.CountAsync());
        Assert.Equal(2, (await CoreAsync(c.Workspace)).Count);
    }

    [Fact]
    public async Task AnInvoiceWhoseBankTotalDiffersStaysOpenAndItsPaymentIsNotRecordedAutomatically()
    {
        var c = await ArrangeAsync("t93-bill-mismatch", seed: false);
        Pluggy.TransactionsByAccount[BankA] = [Tx("pay-bank", BankA, 10, -235m, category: null)];
        SeedCard(Tx("pay-card", CardA, 10, 235m, operation: "PAGAMENTO_FATURA", description: "PAGAMENTO FATURA", type: "CREDIT", category: null));
        SeedBill(235m); // o banco cobra 5,00 que não aparecem como compra (ex.: encargos)
        await LinkBankAsync(c);
        var cardLink = await LinkCardAsync(c, Today.AddDays(-15).Day, Today.AddDays(-8).Day);

        await SyncAsync(c);

        Assert.Empty(await CoreAsync(c.Workspace));
        Assert.Equal(["NoInvoiceForPayment", "NoInvoiceForPayment"], Reasons(await ReviewAsync(c)));
        var linkId = cardLink.GetProperty("link").GetProperty("id").GetGuid();
        var bill = (await JsonGetAsync(c, $"{Integration(c.Workspace)}/links/{linkId}")).GetProperty("reconciliation").GetProperty("card").GetProperty("bills")[0];
        Assert.Equal(("Mismatch", "235.00", "230.00", "5.00"), (bill.GetProperty("status").GetString(), bill.GetProperty("bankTotal").GetString(), bill.GetProperty("saldo360Total").GetString(), bill.GetProperty("difference").GetString()));
        await using var db = Db();
        Assert.Equal(0, await db.FinancialCreditCardInvoicePayments.CountAsync());
        Assert.Equal(0, await db.FinancialCreditCardInvoices.CountAsync(i => i.OfficialVersion > 0));
    }

    [Fact]
    public async Task ADebitThatOnlyMentionsAnInvoiceWithoutCardEvidenceNeverBecomesAnExpense()
    {
        var c = await ArrangeAsync("t93-bill-hint", seed: false);
        Pluggy.TransactionsByAccount[BankA] = [Tx("hint", BankA, 10, -230m, description: "PAGAMENTO DE FATURA", category: null)];
        await LinkBankAsync(c);

        await SyncAsync(c);

        Assert.Empty(await CoreAsync(c.Workspace));
        Assert.Equal(["UnconfirmedCardBillPayment"], Reasons(await ReviewAsync(c)));
    }

    [Fact]
    public async Task AmbiguousBankDebitsForOneCardPaymentAreNotDecidedAndNeverBecomeExpenses()
    {
        var c = await ArrangeAsync("t93-bill-ambiguous", seed: false);
        Pluggy.TransactionsByAccount[BankA] = [Tx("d1", BankA, 10, -150m, category: null), Tx("d2", BankA, 9, -150m, category: null)];
        SeedCard(Tx("pay-card", CardA, 10, 150m, operation: "PAGAMENTO_FATURA", description: "PAGAMENTO FATURA", type: "CREDIT", category: null));
        await LinkBankAsync(c);
        await LinkCardAsync(c, Today.AddDays(-15).Day, Today.AddDays(-8).Day);

        await SyncAsync(c);

        Assert.Empty(await CoreAsync(c.Workspace));
        Assert.Equal(["AmbiguousBankDebit", "AmbiguousCardBillPayment", "AmbiguousCardBillPayment"], Reasons(await ReviewAsync(c)));
        await using var db = Db();
        Assert.Equal(0, await db.FinancialCreditCardInvoicePayments.CountAsync());
    }

    [Fact]
    public async Task APaymentLargerThanTheKnownInvoiceGoesToReview()
    {
        var c = await ArrangeAsync("t93-bill-large", seed: false);
        Pluggy.TransactionsByAccount[BankA] = [Tx("pay-bank", BankA, 10, -1500m, category: null)];
        SeedCard(Tx("pay-card", CardA, 10, 1500m, operation: "PAGAMENTO_FATURA", description: "PAGAMENTO FATURA", type: "CREDIT", category: null));
        await LinkBankAsync(c);
        await LinkCardAsync(c, Today.AddDays(-15).Day, Today.AddDays(-8).Day);

        await SyncAsync(c);

        Assert.Empty(await CoreAsync(c.Workspace));
        Assert.Equal(["NoInvoiceForPayment", "NoInvoiceForPayment"], Reasons(await ReviewAsync(c)));
    }

    // ---- 12. Transferências internas ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task InternalTransfersBetweenIntegratedAccountsDoNotInflateIncomeAndExpense()
    {
        var c = await ArrangeAsync("t93-transfer", seed: false);
        var itemB = await LinkAsync(c.Client, c.Session, c.Workspace, ItemB, "Nubank");
        Pluggy.TransactionsByAccount[BankA] = [Tx("out", BankA, 6, -50m, operation: "TRANSFERENCIA_MESMA_INSTITUICAO", description: "PARA NUBANK", category: "Transfers"), Tx("real", BankA, 5, -7m)];
        Pluggy.TransactionsByAccount[BankB] = [Tx("in", BankB, 6, 50m, operation: "TRANSFERENCIA_MESMA_INSTITUICAO", description: "DE ITAU", category: "Transfers")];
        var a = (await LinkBankAsync(c)).GetProperty("link").GetProperty("financialAccountId").GetGuid();
        var response = await PostAsync(c, $"{Integration(c.Workspace)}/links", new { connectionId = itemB, pluggyAccountId = BankB, mode = "CreateNew", name = "Nubank", financialScope = "PF" });
        var b = (await JsonOf(response)).GetProperty("link").GetProperty("financialAccountId").GetGuid();

        await SyncAsync(c);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(c, $"{Root(c.Workspace)}/connections/{itemB}/sync")).StatusCode);

        await using var db = Db();
        var transfer = await db.FinancialTransfers.AsNoTracking().SingleAsync();
        Assert.Equal((a, b, 50m, false), (transfer.SourceAccountId, transfer.DestinationAccountId, transfer.Amount, transfer.IsCancelled));
        Assert.Equal([7m], (await CoreAsync(c.Workspace)).Select(t => t.Amount));
        var calendar = await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/financial-calendar?from={D(40)}&through={D(0)}");
        Assert.Equal(("0.00", "7.00"), (calendar.GetProperty("summary").GetProperty("realizedIncome").GetString(), calendar.GetProperty("summary").GetProperty("realizedExpense").GetString()));
        Assert.Empty(Reasons(await ReviewAsync(c)));

        await SyncAsync(c);
        Assert.Equal(1, await db.FinancialTransfers.CountAsync());
    }

    [Fact]
    public async Task PixBetweenIntegratedAccountsBecomesATransferAndAlreadyAppliedMovementsAreReplaced()
    {
        var c = await ArrangeAsync("t93-pix-transfer", seed: false);
        var itemB = await LinkAsync(c.Client, c.Session, c.Workspace, ItemB, "Nubank");
        // PIX com terceiros (sem ponta oposta numa conta integrada) segue como receita/despesa.
        Pluggy.TransactionsByAccount[BankA] = [Tx("own-out", BankA, 6, -500m, operation: "PIX", description: "PIX ENVIADO", category: "Transfer - PIX"), Tx("third", BankA, 5, -80m, operation: "PIX", description: "PIX ENVIADO", category: "Transfer - PIX")];
        Pluggy.TransactionsByAccount[BankB] = [];
        var a = (await LinkBankAsync(c)).GetProperty("link").GetProperty("financialAccountId").GetGuid();
        await SyncAsync(c);
        await using var db = Db();
        Assert.Equal([80m, 500m], (await CoreAsync(c.Workspace)).Select(t => t.Amount).Order()); // as duas pontas ainda são despesas isoladas

        Pluggy.TransactionsByAccount[BankB] = [Tx("own-in", BankB, 6, 500m, operation: "PIX", description: "PIX RECEBIDO", category: "Transfer - PIX")];
        var response = await PostAsync(c, $"{Integration(c.Workspace)}/links", new { connectionId = itemB, pluggyAccountId = BankB, mode = "CreateNew", name = "Nubank", financialScope = "PF" });
        var b = (await JsonOf(response)).GetProperty("link").GetProperty("financialAccountId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(c, $"{Root(c.Workspace)}/connections/{itemB}/sync")).StatusCode);

        var transfer = await db.FinancialTransfers.AsNoTracking().SingleAsync();
        Assert.Equal((a, b, 500m, false), (transfer.SourceAccountId, transfer.DestinationAccountId, transfer.Amount, transfer.IsCancelled));
        Assert.Equal([80m], (await CoreAsync(c.Workspace)).Select(t => t.Amount)); // a despesa de 500 foi substituída pela transferência
        var calendar = await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/financial-calendar?from={D(40)}&through={D(0)}");
        Assert.Equal(("0.00", "80.00"), (calendar.GetProperty("summary").GetProperty("realizedIncome").GetString(), calendar.GetProperty("summary").GetProperty("realizedExpense").GetString()));
        Assert.Empty(Reasons(await ReviewAsync(c)));

        await SyncAsync(c);
        Assert.Equal(1, await db.FinancialTransfers.CountAsync(t => !t.IsCancelled));
        Assert.Equal([80m], (await CoreAsync(c.Workspace)).Select(t => t.Amount));
    }

    [Fact]
    public async Task AmbiguousPixCounterpartsGoToReviewInsteadOfGuessing()
    {
        var c = await ArrangeAsync("t93-pix-ambiguous", seed: false);
        var itemB = await LinkAsync(c.Client, c.Session, c.Workspace, ItemB, "Nubank");
        Pluggy.TransactionsByAccount[BankA] = [Tx("out", BankA, 6, -300m, operation: "PIX", description: "PIX ENVIADO", category: "Transfer - PIX")];
        Pluggy.TransactionsByAccount[BankB] = [Tx("in1", BankB, 6, 300m, operation: "PIX", description: "PIX RECEBIDO", category: "Transfer - PIX"), Tx("in2", BankB, 6, 300m, operation: "PIX", description: "PIX RECEBIDO", category: "Transfer - PIX")];
        await LinkBankAsync(c);
        await PostAsync(c, $"{Integration(c.Workspace)}/links", new { connectionId = itemB, pluggyAccountId = BankB, mode = "CreateNew", name = "Nubank", financialScope = "PF" });

        await SyncAsync(c);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(c, $"{Root(c.Workspace)}/connections/{itemB}/sync")).StatusCode);

        await using var db = Db();
        Assert.Equal(0, await db.FinancialTransfers.CountAsync());
        Assert.Equal([300m], (await CoreAsync(c.Workspace)).Select(t => t.Amount)); // a ponta já lançada antes de a outra conta existir permanece como estava
        Assert.Equal(["AmbiguousTransferCounterpart", "AmbiguousTransferCounterpart"], Reasons(await ReviewAsync(c))); // as duas candidatas não são adivinhadas
    }

    [Fact]
    public async Task ASourceChangeOnACardPurchaseUpdatesTheSamePurchaseInsteadOfDuplicatingIt()
    {
        var c = await ArrangeAsync("t93-card-update", seed: false);
        SeedCard();
        SeedBill(240m);
        var cardLink = await LinkCardAsync(c, Today.AddDays(-15).Day, Today.AddDays(-8).Day);
        var cardId = cardLink.GetProperty("link").GetProperty("creditCardId").GetGuid();
        await SyncAsync(c);
        Assert.Equal(0, await Db().FinancialCreditCardInvoices.CountAsync(i => i.OfficialVersion > 0)); // 230 × 240: aberta e divergente

        Pluggy.TransactionsByAccount[CardA] = [.. Pluggy.TransactionsByAccount[CardA].Select(t => t.Id == "c1" ? t with { Amount = 160m } : t)];
        await SyncAsync(c);

        await using var db = Db();
        var purchases = await db.FinancialCreditCardPurchases.AsNoTracking().Include(p => p.Installments).Where(p => p.CreditCardId == cardId).ToListAsync();
        Assert.Equal(2, purchases.Count);
        Assert.Equal(new[] { 80m, 160m }, purchases.Select(p => p.TotalAmount).Order());
        Assert.Equal(240m, purchases.SelectMany(p => p.Installments).Where(i => i.Status == CreditCardInstallmentStatus.Active).Sum(i => i.Amount));
        // Com o total do banco agora igual à soma, a fatura passa a fechar oficialmente.
        Assert.Equal(1, await db.FinancialCreditCardInvoices.CountAsync(i => i.OfficialVersion > 0));

        // Depois de oficial a fatura é imutável: se o banco corrigir o total, a divergência aparece e nada é reescrito sozinho.
        SeedBill(250m);
        Pluggy.TransactionsByAccount[CardA] = [.. Pluggy.TransactionsByAccount[CardA].Select(t => t.Id == "c1" ? t with { Amount = 170m } : t)];
        await SyncAsync(c);
        Assert.Equal(new[] { 80m, 160m }, (await db.FinancialCreditCardPurchases.AsNoTracking().Where(p => p.CreditCardId == cardId).ToListAsync()).Select(p => p.TotalAmount).Order());
        var bill = (await JsonGetAsync(c, $"{Integration(c.Workspace)}/links/{cardLink.GetProperty("link").GetProperty("id").GetGuid()}")).GetProperty("reconciliation").GetProperty("card").GetProperty("bills")[0];
        Assert.Equal(("OfficialDiverges", "10.00"), (bill.GetProperty("status").GetString(), bill.GetProperty("difference").GetString()));
    }

    [Fact]
    public async Task ConsentExpiredIsReflectedInTheIntegrationStatusWithoutTouchingTheAccount()
    {
        var c = await ArrangeAsync("t93-consent");
        await LinkBankAsync(c);
        await SyncAsync(c);
        Pluggy.Items[ItemA] = FakePluggyClient.Item(ItemA, status: "LOGIN_ERROR", consentExpiresAt: DateTimeOffset.UtcNow.AddDays(-1));

        var response = await PostAsync(c, $"{Root(c.Workspace)}/connections/{c.Connection}/sync");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var links = await JsonGetAsync(c, $"{Integration(c.Workspace)}/links");
        Assert.Equal("ConsentExpired", links[0].GetProperty("status").GetString());
        Assert.Equal(3, (await CoreAsync(c.Workspace)).Count); // o que já foi importado continua valendo
    }

    [Fact]
    public async Task WhenOneSideOfAnInternalTransferChangesTheTransferIsUndoneAndReviewed()
    {
        var c = await ArrangeAsync("t93-transfer-change", seed: false);
        var itemB = await LinkAsync(c.Client, c.Session, c.Workspace, ItemB, "Nubank");
        Pluggy.TransactionsByAccount[BankA] = [Tx("out", BankA, 6, -50m, operation: "TRANSFERENCIA_MESMA_INSTITUICAO", description: "PARA NUBANK", category: "Transfers")];
        Pluggy.TransactionsByAccount[BankB] = [Tx("in", BankB, 6, 50m, operation: "TRANSFERENCIA_MESMA_INSTITUICAO", description: "DE ITAU", category: "Transfers")];
        await LinkBankAsync(c);
        await PostAsync(c, $"{Integration(c.Workspace)}/links", new { connectionId = itemB, pluggyAccountId = BankB, mode = "CreateNew", name = "Nubank", financialScope = "PF" });
        await SyncAsync(c);
        await PostAsync(c, $"{Root(c.Workspace)}/connections/{itemB}/sync");
        await using var db = Db();
        Assert.Equal(1, await db.FinancialTransfers.CountAsync(t => !t.IsCancelled));

        Pluggy.TransactionsByAccount[BankA] = [Tx("out", BankA, 6, -55m, operation: "TRANSFERENCIA_MESMA_INSTITUICAO", description: "PARA NUBANK", category: "Transfers")];
        await SyncAsync(c);

        Assert.Equal(0, await db.FinancialTransfers.CountAsync(t => !t.IsCancelled));
        Assert.Equal(["TransferWithoutCounterpart", "TransferWithoutCounterpart"], Reasons(await ReviewAsync(c)));
        Assert.Empty(await CoreAsync(c.Workspace));
    }

    // ---- 13. Estornos e itens sem classificação segura ---------------------------------------------------------------------------

    [Fact]
    public async Task RefundsAndCardCreditsAreReviewedAndNeverIncome()
    {
        var c = await ArrangeAsync("t93-refund", seed: false);
        Pluggy.TransactionsByAccount[BankA] = [Tx("bank-refund", BankA, 4, 30m, operation: "ESTORNO", description: "ESTORNO COMPRA", category: null), Tx("loan", BankA, 4, 3000m, operation: "OPERACAO_CREDITO", category: null)];
        SeedCard(Tx("card-refund", CardA, 3, 40m, operation: "ESTORNO", description: "ESTORNO LOJA", type: "CREDIT", category: null));
        await LinkBankAsync(c);
        await LinkCardAsync(c, Today.AddDays(-15).Day, Today.AddDays(-8).Day);

        await SyncAsync(c);

        Assert.Empty(await CoreAsync(c.Workspace));
        Assert.Equal(["Card:CardRefund", "CreditOperation", "PossibleRefund"], Reasons(await ReviewAsync(c)));
    }

    [Fact]
    public async Task ReviewDecisionsAreValidatedAppliedAndPreservedAcrossSyncs()
    {
        var c = await ArrangeAsync("t93-resolve");
        await LinkBankAsync(c);
        await SyncAsync(c);
        var item = (await ReviewAsync(c)).GetProperty("items")[0];
        var posting = item.GetProperty("postingId").GetGuid();
        var url = $"{Integration(c.Workspace)}/review/{posting}/resolve";
        Assert.Equal(["AsExpense", "Ignore"], item.GetProperty("allowedResolutions").EnumerateArray().Select(x => x.GetString()!));

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(c, url, new { resolution = "AsIncome" })).StatusCode); // débito não vira receita
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(c, url, new { resolution = "AsCardPurchase" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(c, url, new { resolution = "Whatever" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(c, $"{Integration(c.Workspace)}/review/{Guid.NewGuid()}/resolve", new { resolution = "Ignore" })).StatusCode);
        var resolved = await PostAsync(c, url, new { resolution = "AsExpense" });

        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        Assert.Equal("Applied", (await JsonOf(resolved)).GetProperty("state").GetString());
        Assert.Contains(await CoreAsync(c.Workspace), t => t.Amount == 20m && t.Type == CategoryType.Expense);
        Assert.Empty(Reasons(await ReviewAsync(c)));
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(c, url, new { resolution = "Ignore" })).StatusCode); // já decidido
        await SyncAsync(c);
        Assert.Single(await CoreAsync(c.Workspace), t => t.Amount == 20m);
    }

    [Fact]
    public async Task IgnoredReviewItemsStayOutOfTheRealizedForever()
    {
        var c = await ArrangeAsync("t93-ignore");
        await LinkBankAsync(c);
        await SyncAsync(c);
        var posting = (await ReviewAsync(c)).GetProperty("items")[0].GetProperty("postingId").GetGuid();

        var response = await PostAsync(c, $"{Integration(c.Workspace)}/review/{posting}/resolve", new { resolution = "Ignore" });
        await SyncAsync(c);
        await PostAsync(c, $"{Integration(c.Workspace)}/reprocess");

        Assert.Equal("Ignored", (await JsonOf(response)).GetProperty("state").GetString());
        Assert.DoesNotContain(await CoreAsync(c.Workspace), t => t.Amount == 20m);
        Assert.Empty(Reasons(await ReviewAsync(c)));
    }

    // ---- 15. Saldos bancário observado e calculado ----------------------------------------------------------------------------------

    [Fact]
    public async Task ObservedAndCalculatedBalancesStaySeparateAndTheOpeningBalanceIsOnlyAppliedOnExplicitRequest()
    {
        var c = await ArrangeAsync("t93-balance");
        var detail = await LinkBankAsync(c);
        var linkId = detail.GetProperty("link").GetProperty("id").GetGuid();
        var accountId = detail.GetProperty("link").GetProperty("financialAccountId").GetGuid();
        await SyncAsync(c);
        var url = $"{Integration(c.Workspace)}/links/{linkId}";

        var before = (await JsonGetAsync(c, url)).GetProperty("reconciliation");
        Assert.Equal("1500.75", before.GetProperty("observedBalance").GetString());
        Assert.Equal(JsonValueKind.Null, before.GetProperty("calculatedBalance").ValueKind); // sem saldo inicial: indisponível, nunca zero
        Assert.Equal("OpeningBalanceNotConfigured", before.GetProperty("calculatedUnavailableReason").GetString());
        Assert.Equal(JsonValueKind.Null, before.GetProperty("difference").ValueKind);
        Assert.Equal("ReviewPending", before.GetProperty("suggestionUnavailableReason").GetString()); // há movimento em revisão
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(c, $"{url}/opening-balance-suggestion", new { confirm = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(c, $"{url}/opening-balance-suggestion", new { })).StatusCode);

        var posting = (await ReviewAsync(c)).GetProperty("items")[0].GetProperty("postingId").GetGuid();
        await PostAsync(c, $"{Integration(c.Workspace)}/review/{posting}/resolve", new { resolution = "AsExpense" });
        var suggestion = (await JsonGetAsync(c, url)).GetProperty("reconciliation");
        // 1500.75 observado − (99.90 − 10.25 − 7.00 − 20.00) = 1438.10 no início do primeiro dia de histórico.
        Assert.Equal("1438.10", suggestion.GetProperty("suggestedOpeningBalance").GetString());

        var applied = await PostAsync(c, $"{url}/opening-balance-suggestion", new { confirm = true });
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        var after = (await JsonOf(applied)).GetProperty("reconciliation");
        Assert.Equal(("1500.75", "1500.75", "0.00"), (after.GetProperty("observedBalance").GetString(), after.GetProperty("calculatedBalance").GetString(), after.GetProperty("difference").GetString()));
        var balance = await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/accounts/{accountId}/balance");
        Assert.Equal("1500.75", balance.GetProperty("balance").GetString());

        // Uma segunda aplicação não substitui o saldo inicial sem confirmação específica.
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(c, $"{url}/opening-balance-suggestion", new { confirm = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(c, $"{url}/opening-balance-suggestion", new { confirm = true, replaceExisting = true })).StatusCode);
    }

    [Fact]
    public async Task CardsExposeLimitsSeparatelyAndNeverACashBalance()
    {
        var c = await ArrangeAsync("t93-card-balance", seed: false);
        SeedCard();
        var link = await LinkCardAsync(c, Today.AddDays(-15).Day, Today.AddDays(-8).Day);
        await SyncAsync(c);

        var detail = await JsonGetAsync(c, $"{Integration(c.Workspace)}/links/{link.GetProperty("link").GetProperty("id").GetGuid()}");

        var reconciliation = detail.GetProperty("reconciliation");
        Assert.Equal(JsonValueKind.Null, reconciliation.GetProperty("calculatedBalance").ValueKind);
        var card = reconciliation.GetProperty("card");
        Assert.Equal(("5000.00", "230.00", "4770.00", "800.00"), (card.GetProperty("creditLimit").GetString(), card.GetProperty("usedLimit").GetString(), card.GetProperty("availableLimit").GetString(),
            card.GetProperty("observedAvailableLimit").GetString()));
    }

    // ---- 16. Dashboard e Agenda ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task DashboardAndCalendarReflectTheImportedMovementsSeparatingRealizedFromPlanned()
    {
        var c = await ArrangeAsync("t93-dashboard");
        var detail = await LinkBankAsync(c);
        var linkId = detail.GetProperty("link").GetProperty("id").GetGuid();
        await SyncAsync(c);
        var posting = (await ReviewAsync(c)).GetProperty("items")[0].GetProperty("postingId").GetGuid();
        await PostAsync(c, $"{Integration(c.Workspace)}/review/{posting}/resolve", new { resolution = "Ignore" });
        await PostAsync(c, $"{Integration(c.Workspace)}/links/{linkId}/opening-balance-suggestion", new { confirm = true });

        var calendar = await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/financial-calendar?from={D(40)}&through={D(0)}");
        var items = calendar.GetProperty("items").EnumerateArray().ToList();

        Assert.Equal(("99.90", "17.25", "0.00", "0.00"), (calendar.GetProperty("summary").GetProperty("realizedIncome").GetString(), calendar.GetProperty("summary").GetProperty("realizedExpense").GetString(),
            calendar.GetProperty("summary").GetProperty("pendingIncome").GetString(), calendar.GetProperty("summary").GetProperty("pendingExpense").GetString()));
        Assert.Equal(3, items.Count);
        Assert.All(items, i => Assert.Equal(("Realized", "PluggyImport"), (i.GetProperty("status").GetString(), i.GetProperty("origin").GetString())));

        var start = Today.AddDays(-9);
        var dashboard = await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/financial-dashboard?competence={start:yyyy-MM}&months=2");
        var position = dashboard.GetProperty("position");
        Assert.Equal(("1500.75", 1), (position.GetProperty("realizedBalance").GetString(), position.GetProperty("consideredAccounts").GetInt32()));
        var months = dashboard.GetProperty("monthlyProjection").EnumerateArray().ToList();
        Assert.Equal(99.90m, months.Sum(m => decimal.Parse(m.GetProperty("realizedIncome").GetString()!, System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(17.25m, months.Sum(m => decimal.Parse(m.GetProperty("realizedExpense").GetString()!, System.Globalization.CultureInfo.InvariantCulture)));
    }
}
