using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Pluggy;
using Saldo360.Domain.Accounts;
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
        // Fechada pelo banco e paga, independentemente da conciliação: o histórico importado começa dentro deste ciclo, então a conciliação é incompleta.
        Assert.Equal(("Closed", "Paid", "Incomplete", "MissingHistory"), (bill.GetProperty("cycle").GetString(), bill.GetProperty("payment").GetString(), bill.GetProperty("reconciliation").GetString(), bill.GetProperty("reconciliationReason").GetString()));
        Assert.Equal(("230.00", "230.00", "0.00"), (bill.GetProperty("bankTotal").GetString(), bill.GetProperty("saldo360Total").GetString(), bill.GetProperty("difference").GetString()));
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
    public async Task APersonalAccountPaysABusinessCardWithOneCashOutflowAndNeitherASecondConsumptionNorATransfer()
    {
        var c = await ArrangeAsync("t933-pf-pays-pj", seed: false);
        var itemB = await LinkAsync(c.Client, c.Session, c.Workspace, ItemB, "Itaú PF");
        // Item A: conta e cartão da PJ. Item B: conta da PF, de onde sai o dinheiro que paga o cartão PJ.
        Pluggy.TransactionsByAccount[BankA] = [];
        Pluggy.TransactionsByAccount[BankB] = [Tx("pay-bank", BankB, 10, -230m, operation: "PIX", description: "PAGAMENTO DE FATURA", category: null)];
        SeedCard(Tx("pay-card", CardA, 10, 230m, operation: "PAGAMENTO_FATURA", description: "PAGAMENTO FATURA", type: "CREDIT", category: null));
        var pj = (await LinkBankAsync(c, name: "Itaú PJ", extra: new { financialScope = "PJ" })).GetProperty("link").GetProperty("financialAccountId").GetGuid();
        var pfLink = await PostAsync(c, $"{Integration(c.Workspace)}/links", new { connectionId = itemB, pluggyAccountId = BankB, mode = "CreateNew", name = "Itaú PF", financialScope = "PF" });
        Assert.Equal(HttpStatusCode.Created, pfLink.StatusCode);
        var pf = (await JsonOf(pfLink)).GetProperty("link").GetProperty("financialAccountId").GetGuid();
        var cardResponse = await PostAsync(c, $"{Integration(c.Workspace)}/links", new
        {
            connectionId = c.Connection, pluggyAccountId = CardA, mode = "CreateNew", name = "Itaú Empresas Visa",
            closingDay = Today.AddDays(-15).Day, dueDay = Today.AddDays(-8).Day, paymentAccountId = pf,
        });
        Assert.Equal(HttpStatusCode.Created, cardResponse.StatusCode);
        var cardId = (await JsonOf(cardResponse)).GetProperty("link").GetProperty("creditCardId").GetGuid();

        await SyncAsync(c);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(c, $"{Root(c.Workspace)}/connections/{itemB}/sync")).StatusCode);

        // O cartão continua identificado como da PJ (escopo da conexão onde ele nasceu); a pagadora é só uma preferência.
        var card = await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/credit-cards/{cardId}");
        Assert.Equal(("PJ", "Pluggy", pf), (card.GetProperty("scope").GetString(), card.GetProperty("origin").GetString(), card.GetProperty("defaultPaymentAccountId").GetGuid()));

        // Uma única saída de caixa, na conta PF; liquida a fatura do cartão PJ sem segunda despesa de consumo e sem transferência PF/PJ.
        var core = await CoreAsync(c.Workspace);
        var payment = Assert.Single(core);
        Assert.Equal((FinancialTransactionOrigin.CreditCardInvoicePayment, CategoryType.Expense, pf, 230m), (payment.Origin, payment.Type, payment.AccountId, payment.Amount));
        await using var db = Db();
        Assert.Equal(0, await db.FinancialTransfers.CountAsync());
        var invoicePayment = await db.FinancialCreditCardInvoicePayments.AsNoTracking().SingleAsync();
        Assert.Equal((payment.Id, 230m), (invoicePayment.TransactionId, invoicePayment.Amount));
        var invoice = (await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/credit-cards/{cardId}/invoices?from={Today.AddMonths(-3):yyyy-MM}&through={Today.AddMonths(3):yyyy-MM}")).EnumerateArray().Single();
        Assert.Equal(("230.00", "Paid"), (invoice.GetProperty("paidAmount").GetString(), invoice.GetProperty("paymentStatus").GetString()));
        // Consumo por cartão e caixa por conta pagadora são consultas separadas: a PF tem a saída, a PJ nenhuma movimentação.
        Assert.Equal(1, await db.FinancialTransactions.CountAsync(t => t.AccountId == pf));
        Assert.Equal(0, await db.FinancialTransactions.CountAsync(t => t.AccountId == pj));

        // Trocar a conta pagadora padrão depois não mexe no histórico: o pagamento segue na conta que realmente pagou.
        var changed = await Send(c.Client, HttpMethod.Put, $"/api/financial-workspaces/{c.Workspace}/credit-cards/{cardId}", c.Session, new
        {
            name = card.GetProperty("name").GetString(), issuer = card.GetProperty("issuer").GetString(), lastFourDigits = card.GetProperty("lastFourDigits").GetString(),
            creditLimit = card.GetProperty("creditLimit").GetString(), closingDay = card.GetProperty("closingDay").GetInt32(), dueDay = card.GetProperty("dueDay").GetInt32(),
            defaultPaymentAccountId = pj,
        });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        await SyncAsync(c);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(c, $"{Root(c.Workspace)}/connections/{itemB}/sync")).StatusCode);
        var afterChange = Assert.Single(await CoreAsync(c.Workspace));
        Assert.Equal((payment.Id, pf), (afterChange.Id, afterChange.AccountId));
        Assert.Equal(1, await db.FinancialCreditCardInvoicePayments.CountAsync());
        Assert.Equal(0, await db.FinancialTransfers.CountAsync());
    }

    [Fact]
    public async Task ABillClosedByTheBankIsClosedAndPaidEvenWhenItsTotalDiffersFromTheImportedPurchases()
    {
        var c = await ArrangeAsync("t93-bill-mismatch", seed: false);
        Pluggy.TransactionsByAccount[BankA] = [Tx("pay-bank", BankA, 10, -235m, category: null)];
        SeedCard(Tx("pay-card", CardA, 10, 235m, operation: "PAGAMENTO_FATURA", description: "PAGAMENTO FATURA", type: "CREDIT", category: null));
        SeedBill(235m); // o banco cobra 5,00 que não aparecem como compra (ex.: encargos)
        await LinkBankAsync(c);
        var cardLink = await LinkCardAsync(c, Today.AddDays(-15).Day, Today.AddDays(-8).Day);

        await SyncAsync(c);

        // Fechamento e pagamento não dependem da conciliação: a saída bancária quita a fatura e a diferença só é registrada.
        Assert.Equal(["Payment"], (await CoreAsync(c.Workspace)).Select(t => t.Amount == 235m && t.Origin == FinancialTransactionOrigin.CreditCardInvoicePayment ? "Payment" : "Other"));
        Assert.Empty(Reasons(await ReviewAsync(c)));
        var bill = (await BillsOfAsync(c, cardLink))[0];
        Assert.Equal(("Closed", "Paid", "Incomplete"), States(bill));
        Assert.Equal(("235.00", "230.00", "5.00"), (bill.GetProperty("bankTotal").GetString(), bill.GetProperty("saldo360Total").GetString(), bill.GetProperty("difference").GetString()));
        await using var db = Db();
        var invoice = await db.FinancialCreditCardInvoices.AsNoTracking().SingleAsync();
        Assert.Equal((235m, 0), (invoice.InstitutionTotal, invoice.OfficialVersion));
        Assert.Equal(1, await db.FinancialCreditCardInvoicePayments.CountAsync());
        Assert.Equal(0, await db.FinancialCreditCardInvoiceAdjustments.CountAsync()); // nenhum ajuste para "compensar" a diferença
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
        Assert.Equal(1, await Db().FinancialCreditCardInvoices.CountAsync(i => i.InstitutionClosedAt != null)); // fechada pelo banco com total 240; as compras somam 230 (divergente)

        Pluggy.TransactionsByAccount[CardA] = [.. Pluggy.TransactionsByAccount[CardA].Select(t => t.Id == "c1" ? t with { Amount = 160m } : t)];
        await SyncAsync(c);

        await using var db = Db();
        var purchases = await db.FinancialCreditCardPurchases.AsNoTracking().Include(p => p.Installments).Where(p => p.CreditCardId == cardId).ToListAsync();
        Assert.Equal(2, purchases.Count);
        Assert.Equal(new[] { 80m, 160m }, purchases.Select(p => p.TotalAmount).Order());
        Assert.Equal(240m, purchases.SelectMany(p => p.Installments).Where(i => i.Status == CreditCardInstallmentStatus.Active).Sum(i => i.Amount));
        Assert.Equal(0, await db.FinancialCreditCardInvoices.CountAsync(i => i.OfficialVersion > 0));

        // Fatura fechada pelo banco não é imutável: se o banco corrige o total e o valor da compra, a mesma compra é atualizada e a fatura segue fechada.
        SeedBill(250m);
        Pluggy.TransactionsByAccount[CardA] = [.. Pluggy.TransactionsByAccount[CardA].Select(t => t.Id == "c1" ? t with { Amount = 170m } : t)];
        await SyncAsync(c);
        Assert.Equal(new[] { 80m, 170m }, (await db.FinancialCreditCardPurchases.AsNoTracking().Where(p => p.CreditCardId == cardId).ToListAsync()).Select(p => p.TotalAmount).Order());
        Assert.Equal(250m, (await db.FinancialCreditCardInvoices.AsNoTracking().SingleAsync(i => i.InstitutionClosedAt != null)).InstitutionTotal);
        var bill = (await JsonGetAsync(c, $"{Integration(c.Workspace)}/links/{cardLink.GetProperty("link").GetProperty("id").GetGuid()}")).GetProperty("reconciliation").GetProperty("card").GetProperty("bills")[0];
        Assert.Equal("Closed", bill.GetProperty("cycle").GetString());
        Assert.Equal("250.00", bill.GetProperty("bankTotal").GetString());
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
        Assert.Equal(["CreditOperation", "CreditWithoutBillReference", "PossibleRefund"], Reasons(await ReviewAsync(c))); // sem a fatura de referência, nada é adivinhado
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
        Pluggy.AccountsByItem[ItemA] = [FakePluggyClient.Account(BankA, ItemA) with { UpdatedAt = DateTimeOffset.UtcNow }, FakePluggyClient.Account(CardA, ItemA, "CREDIT", "5555444433332222")];
        var detail = await LinkBankAsync(c);
        var linkId = detail.GetProperty("link").GetProperty("id").GetGuid();
        var accountId = detail.GetProperty("link").GetProperty("financialAccountId").GetGuid();
        await SyncAsync(c);
        var url = $"{Integration(c.Workspace)}/links/{linkId}";

        var before = (await JsonGetAsync(c, url)).GetProperty("reconciliation");
        Assert.Equal("1500.75", before.GetProperty("observedBalance").GetString());
        // Sem saldo inicial manual: o saldo calculado parte da referência bancária (âncora) e dos movimentos consistentes; a diferença seria zero por construção e não é exibida.
        Assert.Equal("1500.75", before.GetProperty("calculatedBalance").GetString());
        Assert.Equal(JsonValueKind.Null, before.GetProperty("calculatedUnavailableReason").ValueKind);
        Assert.Equal(JsonValueKind.Null, before.GetProperty("difference").ValueKind);
        Assert.Null((await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/accounts/{accountId}")).GetProperty("openingBalance").GetString()); // nada foi gravado na conta
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

    // ============================================================================================================================
    // T09.3.2: fatura fechada pela instituição, pagamentos, créditos, âncora de saldo e fechamento/vencimento
    // ============================================================================================================================

    private static PluggyBill Bill(string id, int closedDaysAgo, int dueDaysAgo, decimal total, IEnumerable<(decimal Amount, int DaysAgo)>? payments = null, IEnumerable<decimal>? charges = null) =>
        new(id, DateTimeOffset.Parse(D(dueDaysAgo) + "T00:00:00Z"), total, "BRL", null, null,
            (charges ?? []).Select(a => new PluggyFinanceCharge("IOF", a, "BRL", null)).ToList(), DateTimeOffset.Parse(D(closedDaysAgo) + "T00:00:00Z"),
            (payments ?? []).Select(p => new PluggyBillPayment(p.Amount, "BRL", DateTimeOffset.Parse(D(p.DaysAgo) + "T00:00:00Z"), "DEBIT_ACCOUNT", null)).ToList());

    private static PluggyBill FixedBill(string id, DateOnly closing, DateOnly due) =>
        new(id, DateTimeOffset.Parse(due.ToString("yyyy-MM-dd") + "T00:00:00Z"), 0m, "BRL", null, null, [], DateTimeOffset.Parse(closing.ToString("yyyy-MM-dd") + "T00:00:00Z"), []);

    private static PluggyCardMetadata OnBill(string id) => new(null, null, null, null, id, "2222", null, null);

    /// <summary>Dois ciclos: "bill-0" (fechou há 46 dias, com a compra mais antiga do histórico) e "bill-1" (fechou há 15 dias), com 230,00 em compras.</summary>
    private void SeedCycle(decimal bill1Total, IEnumerable<(decimal Amount, int DaysAgo)>? payments = null, IEnumerable<decimal>? charges = null, params PluggyTransaction[] extra)
    {
        Pluggy.BillsByAccount[CardA] = [Bill("bill-1", 15, 8, bill1Total, payments, charges), Bill("bill-0", 46, 39, 10m)];
        Pluggy.TransactionsByAccount[CardA] =
        [
            Tx("c-old", CardA, 60, 10m, operation: "PAGAMENTO", description: "ANTIGA", card: OnBill("bill-0"), type: "DEBIT", category: null),
            Tx("c1", CardA, 20, 150m, operation: "PAGAMENTO", description: "LOJA", card: OnBill("bill-1"), type: "DEBIT", category: null),
            Tx("c2", CardA, 19, 80m, operation: "PAGAMENTO", description: "TV", card: OnBill("bill-1"), type: "DEBIT", category: null),
            ..extra,
        ];
    }

    private async Task<List<JsonElement>> BillsOfAsync(Ctx c, JsonElement cardLink)
    {
        var linkId = cardLink.GetProperty("link").GetProperty("id").GetGuid();
        return (await JsonGetAsync(c, $"{Integration(c.Workspace)}/links/{linkId}")).GetProperty("reconciliation").GetProperty("card").GetProperty("bills").EnumerateArray().ToList();
    }

    private static (string Cycle, string Payment, string Reconciliation) States(JsonElement bill) =>
        (bill.GetProperty("cycle").GetString()!, bill.GetProperty("payment").GetString()!, bill.GetProperty("reconciliation").GetString()!);

    private static string Money2(JsonElement bill, string property) => bill.GetProperty(property).GetString()!;

    private static PluggyTransaction BankPayment(string id, int daysAgo, decimal amount) =>
        Tx(id, BankA, daysAgo, -amount, operation: "BOLETO", description: "PAGAMENTO", category: "Credit card payment");

    // ---- 1 a 5. Fechamento pela instituição, independente da conciliação ----------------------------------------------------

    [Fact]
    public async Task ABillClosedByTheBankWithEverythingImportedIsClosedUnpaidAndReconciled()
    {
        var c = await ArrangeAsync("t932-reconciled", seed: false);
        SeedCycle(230m);
        var cardLink = await LinkCardAsync(c, 3, 12); // dias habituais diferentes dos do banco: as compras precisam migrar para a fatura real

        await SyncAsync(c);

        var bill = (await BillsOfAsync(c, cardLink))[0];
        Assert.Equal(("Closed", "Unpaid", "Reconciled"), States(bill));
        Assert.Equal(("230.00", "230.00", "0.00"), (Money2(bill, "bankTotal"), Money2(bill, "saldo360Total"), Money2(bill, "difference")));
        await using var db = Db();
        var invoices = await db.FinancialCreditCardInvoices.AsNoTracking().ToListAsync();
        Assert.All(invoices, i => Assert.Equal("Pluggy", i.InstitutionSource)); // só existem faturas do banco: as de calendário foram removidas depois de as compras migrarem
        var closed = invoices.Single(i => i.ClosingDate == Today.AddDays(-15));
        Assert.Equal((230m, 0), (closed.InstitutionTotal, closed.OfficialVersion));
        Assert.Equal(0, await db.FinancialCreditCardInvoiceAdjustments.CountAsync());
        Assert.All(await db.FinancialCreditCardInstallments.AsNoTracking().ToListAsync(), i => Assert.Contains(i.InvoiceId, invoices.Select(x => (Guid?)x.Id)));
    }

    [Fact]
    public async Task ABillClosedByTheBankWithMissingPurchasesIsDivergentAndTheBankTotalIsPreservedWithoutAnyCompensation()
    {
        var c = await ArrangeAsync("t932-missing", seed: false);
        SeedCycle(500m); // o banco cobra 270,00 que não aparecem como compras
        var cardLink = await LinkCardAsync(c, 3, 12);

        await SyncAsync(c);

        var bill = (await BillsOfAsync(c, cardLink))[0];
        Assert.Equal(("Closed", "Unpaid", "Divergent"), States(bill));
        Assert.Equal(("500.00", "230.00", "270.00", "TotalMismatch"), (Money2(bill, "bankTotal"), Money2(bill, "saldo360Total"), Money2(bill, "difference"), Money2(bill, "reconciliationReason")));
        await using var db = Db();
        Assert.Equal(500m, (await db.PluggyCardBills.AsNoTracking().SingleAsync(b => b.PluggyBillId == "bill-1")).TotalAmount);
        Assert.Equal(500m, (await db.FinancialCreditCardInvoices.AsNoTracking().SingleAsync(i => i.ClosingDate == Today.AddDays(-15))).InstitutionTotal);
        Assert.Equal(0, await db.FinancialCreditCardInvoiceAdjustments.CountAsync()); // nenhum ajuste fictício
        Assert.Equal(0, await db.FinancialTransactions.CountAsync());
    }

    [Fact]
    public async Task PendingPurchasesNeverChangeTheClosureReportedByTheBankAndMakeTheReconciliationIncomplete()
    {
        var c = await ArrangeAsync("t932-pending", seed: false);
        SeedCycle(230m, extra: [Tx("pending", CardA, 3, 30m, "PENDING", "PAGAMENTO", "AGUARDANDO", card: OnBill("bill-1"), type: "DEBIT", category: null)]);
        var cardLink = await LinkCardAsync(c, 3, 12);

        await SyncAsync(c);

        var bill = (await BillsOfAsync(c, cardLink))[0];
        Assert.Equal(("Closed", "Unpaid", "Incomplete"), States(bill));
        Assert.Equal("OpenItems", Money2(bill, "reconciliationReason"));
        await using var db = Db();
        Assert.Equal(230m, (await db.FinancialCreditCardInvoices.AsNoTracking().SingleAsync(i => i.ClosingDate == Today.AddDays(-15))).InstitutionTotal);
    }

    [Fact]
    public async Task FinanceChargesAreCardConsumptionAndABillWithThemIsReconciled()
    {
        var c = await ArrangeAsync("t932-charges", seed: false);
        SeedCycle(240m, charges: [10m], extra: [Tx("iof", CardA, 18, 10m, operation: "OUTROS", description: "IOF", card: OnBill("bill-1"), type: "DEBIT", category: "Tax on financial operations")]);
        var cardLink = await LinkCardAsync(c, 3, 12);

        await SyncAsync(c);

        var bill = (await BillsOfAsync(c, cardLink))[0];
        Assert.Equal(("Closed", "Unpaid", "Reconciled"), States(bill));
        Assert.Equal(("240.00", "10.00"), (Money2(bill, "saldo360Total"), Money2(bill, "financeCharges")));
        await using var db = Db();
        Assert.Equal(4, await db.FinancialCreditCardPurchases.CountAsync()); // o encargo entra como consumo do cartão (uma compra de uma parcela)
        Assert.Empty(await CoreAsync(c.Workspace)); // e nunca como lançamento bancário nem receita
    }

    [Fact]
    public async Task RefundsAndCashbackWithABillReferenceBecomeInvoiceCreditsNeverIncomeAndAreNotDuplicatedOnReprocess()
    {
        var c = await ArrangeAsync("t932-credits", seed: false);
        var shopping = await CreateCategoryAsync(c, "Compras", "Expense");
        SeedCycle(185m, extra:
        [
            Tx("refund", CardA, 12, 40m, operation: "ESTORNO", description: "ESTORNO LOJA", card: OnBill("bill-1"), type: "CREDIT", category: "Shopping"),
            Tx("cashback", CardA, 11, 5m, operation: "OUTROS", description: "CASHBACK", card: OnBill("bill-1"), type: "CREDIT", category: "Cashback"),
        ]);
        var cardLink = await LinkCardAsync(c, 3, 12);

        await SyncAsync(c);
        await PostAsync(c, $"{Integration(c.Workspace)}/reprocess");
        await PostAsync(c, $"{Integration(c.Workspace)}/reprocess");

        var bill = (await BillsOfAsync(c, cardLink))[0];
        Assert.Equal(("Closed", "Unpaid", "Reconciled"), States(bill)); // 230 - 40 - 5 = 185
        await using var db = Db();
        var adjustments = await db.FinancialCreditCardInvoiceAdjustments.AsNoTracking().OrderBy(a => a.Amount).ToListAsync();
        Assert.Equal([5m, 40m], adjustments.Select(a => a.Amount));
        Assert.All(adjustments, a => { Assert.Equal(FinancialCreditCardInvoiceAdjustmentNature.Credit, a.Nature); Assert.StartsWith("pluggy:", a.ExternalId); });
        Assert.Equal([FinancialCreditCardInvoiceAdjustmentType.Credit, FinancialCreditCardInvoiceAdjustmentType.Refund], adjustments.Select(a => a.Type));
        Assert.Equal(shopping, adjustments.Single(a => a.Type == FinancialCreditCardInvoiceAdjustmentType.Refund).CategoryId); // o estorno reduz o consumo da categoria da compra
        Assert.Equal(2, await db.PluggyPostings.CountAsync(p => p.Kind == PluggyPostingKind.CardCredit && p.State == PluggyPostingState.Applied));
        Assert.Empty(await CoreAsync(c.Workspace)); // nada de receita
    }

    [Fact]
    public async Task ACardCreditWhoseSourceChangesIsRecreatedWithANewExternalIdInsteadOfBreakingTheSync()
    {
        var c = await ArrangeAsync("t933-credit-change", seed: false);
        SeedCycle(185m, extra: [Tx("refund", CardA, 12, 40m, operation: "ESTORNO", description: "ESTORNO LOJA", card: OnBill("bill-1"), type: "CREDIT", category: "Shopping")]);
        await LinkCardAsync(c, 3, 12);
        await SyncAsync(c);

        // O banco corrige o valor duas vezes: cada correção cancela o ajuste anterior e cria o novo, sem violar o índice único.
        foreach (var amount in new[] { 50m, 60m })
        {
            SeedCycle(185m, extra: [Tx("refund", CardA, 12, amount, operation: "ESTORNO", description: "ESTORNO LOJA", card: OnBill("bill-1"), type: "CREDIT", category: "Shopping")]);
            await SyncAsync(c);
        }

        await using var db = Db();
        var adjustments = await db.FinancialCreditCardInvoiceAdjustments.AsNoTracking().OrderBy(a => a.CreatedAt).ThenBy(a => a.Amount).ToListAsync();
        Assert.Equal(3, adjustments.Count);
        Assert.Equal(3, adjustments.Select(a => a.ExternalId).Distinct().Count());
        Assert.All(adjustments, a => Assert.StartsWith("pluggy:", a.ExternalId));
        var active = Assert.Single(adjustments, a => a.Status == FinancialCreditCardInvoiceAdjustmentStatus.Active);
        Assert.Equal(60m, active.Amount);
        Assert.Equal(2, adjustments.Count(a => a.Status == FinancialCreditCardInvoiceAdjustmentStatus.Cancelled));
        var posting = await db.PluggyPostings.AsNoTracking().SingleAsync(p => p.Kind == PluggyPostingKind.CardCredit);
        Assert.Equal((PluggyPostingState.Applied, active.Id), (posting.State, posting.CreditCardInvoiceAdjustmentId));

        await SyncAsync(c); // sem mudança na origem nada é recriado
        Assert.Equal(3, await db.FinancialCreditCardInvoiceAdjustments.CountAsync());
        Assert.Empty(await CoreAsync(c.Workspace)); // nunca vira receita
    }

    [Fact]
    public async Task APaymentAlreadyRecordedInAnEarlierRunIsNeverPairedAgainWithAnotherLookAlikeDebit()
    {
        var c = await ArrangeAsync("t933-payment-rerun", seed: false);
        // A fatura (460) ainda comporta outro pagamento de 230: sem a proteção, o payments[] seria reaproveitado por um segundo débito igual.
        Pluggy.TransactionsByAccount[BankA] = [BankPayment("pay", 10, 230m)];
        SeedCycle(460m, payments: [(230m, 10)], extra: [Tx("pay-card", CardA, 10, 230m, operation: "PAGAMENTO_FATURA", description: "PAGAMENTO FATURA", type: "CREDIT", category: null)]);
        await LinkBankAsync(c);
        await LinkCardAsync(c, 3, 12);
        await SyncAsync(c);
        await using var db = Db();
        Assert.Equal(1, await db.FinancialCreditCardInvoicePayments.CountAsync());

        Pluggy.TransactionsByAccount[BankA] = [BankPayment("pay", 10, 230m), BankPayment("look-alike", 9, 230m)];
        await SyncAsync(c);
        await PostAsync(c, $"{Integration(c.Workspace)}/reprocess");

        Assert.Equal(1, await db.FinancialCreditCardInvoicePayments.CountAsync(p => p.Status == FinancialCreditCardInvoicePaymentStatus.Active));
        var lookAlike = await db.PluggyPostings.AsNoTracking().SingleAsync(p => db.PluggyImportedTransactions.Any(t => t.Id == p.ImportedTransactionId && t.PluggyTransactionId == "look-alike"));
        Assert.NotEqual(PluggyPostingKind.CardBillPayment, lookAlike.Kind);
        Assert.Equal(1, await db.FinancialTransactions.CountAsync(t => t.Origin == FinancialTransactionOrigin.CreditCardInvoicePayment && !t.IsCancelled));
    }

    // ---- 6 a 11. Pagamentos --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFullBankPaymentRecognizedByThePaymentsOfTheBillSettlesTheInvoiceWithoutDuplicatingConsumption()
    {
        var c = await ArrangeAsync("t932-paid", seed: false);
        Pluggy.TransactionsByAccount[BankA] = [BankPayment("pay", 10, 230m), Tx("other", BankA, 4, -12m)];
        SeedCycle(230m, payments: [(230m, 10)]);
        await LinkBankAsync(c);
        var cardLink = await LinkCardAsync(c, 3, 12);

        await SyncAsync(c);

        var bill = (await BillsOfAsync(c, cardLink))[0];
        Assert.Equal(("Closed", "Paid", "Reconciled"), States(bill));
        Assert.Equal(("230.00", "230.00"), (Money2(bill, "paidAmount"), Money2(bill, "bankReportedPaid")));
        var core = await CoreAsync(c.Workspace);
        Assert.Equal(2, core.Count);
        var payment = core.Single(t => t.Amount == 230m);
        Assert.Equal(FinancialTransactionOrigin.CreditCardInvoicePayment, payment.Origin);
        Assert.Null(payment.CategoryId); // saída de caixa sem categoria de consumo
        await using var db = Db();
        Assert.Equal(1, await db.FinancialCreditCardInvoicePayments.CountAsync());
        Assert.Equal(PluggyPostingKind.CardBillPayment, (await db.PluggyPostings.AsNoTracking().SingleAsync(p => p.FinancialTransactionId == payment.Id)).Kind);
        // Caixa: só o pagamento (230) e a outra despesa (12) aparecem; as compras (230) ficam no cartão e não somam outra vez.
        var calendar = await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/financial-calendar?from={D(40)}&through={D(0)}");
        Assert.Equal("242.00", calendar.GetProperty("summary").GetProperty("realizedExpense").GetString());

        await SyncAsync(c);
        Assert.Equal(1, await db.FinancialCreditCardInvoicePayments.CountAsync()); // idempotente
    }

    [Fact]
    public async Task APartialPaymentSettlesPartOfTheInvoiceAndLeavesItPartiallyPaid()
    {
        var c = await ArrangeAsync("t932-partial", seed: false);
        Pluggy.TransactionsByAccount[BankA] = [BankPayment("pay", 10, 100m)];
        SeedCycle(230m, payments: [(100m, 10)]);
        await LinkBankAsync(c);
        var cardLink = await LinkCardAsync(c, 3, 12);

        await SyncAsync(c);

        var bill = (await BillsOfAsync(c, cardLink))[0];
        Assert.Equal(("Closed", "PartiallyPaid", "Reconciled"), States(bill));
        Assert.Equal("100.00", Money2(bill, "paidAmount"));
        await using var db = Db();
        Assert.Equal(100m, (await db.FinancialCreditCardInvoicePayments.AsNoTracking().SingleAsync()).Amount);
    }

    [Fact]
    public async Task AnAmbiguousPaymentIsNeverDecidedAndNothingIsSettled()
    {
        var c = await ArrangeAsync("t932-ambiguous", seed: false);
        Pluggy.TransactionsByAccount[BankA] = [BankPayment("pay", 10, 230m)];
        SeedCycle(230m, payments: [(230m, 10)]);
        // Outra fatura do mesmo cartão informa um pagamento idêntico (mesmo valor e data): um débito só não pode quitar as duas.
        Pluggy.BillsByAccount[CardA] = [.. Pluggy.BillsByAccount[CardA], Bill("bill-x", 25, 18, 230m, payments: [(230m, 10)])];
        await LinkBankAsync(c);
        await LinkCardAsync(c, 3, 12);

        await SyncAsync(c);

        Assert.Empty(await CoreAsync(c.Workspace));
        Assert.Equal(["AmbiguousCardBillPayment"], Reasons(await ReviewAsync(c)));
        await using var db = Db();
        Assert.Equal(0, await db.FinancialCreditCardInvoicePayments.CountAsync());
    }

    [Fact]
    public async Task APaymentFromABankAccountThatIsNotIntegratedIsNotInventedButTheBankReportedPaymentIsShown()
    {
        var c = await ArrangeAsync("t932-no-bank", seed: false);
        SeedCycle(230m, payments: [(230m, 10)], extra: [Tx("pay-card", CardA, 10, 230m, operation: "PAGAMENTO_FATURA", description: "PAGAMENTO FATURA", card: OnBill("bill-1"), type: "CREDIT", category: null)]);
        var cardLink = await LinkCardAsync(c, 3, 12);

        await SyncAsync(c);

        var bill = (await BillsOfAsync(c, cardLink))[0];
        Assert.Equal(("Closed", "Paid", "Reconciled"), States(bill)); // o banco informa o pagamento
        Assert.Equal(("0.00", "230.00"), (Money2(bill, "paidAmount"), Money2(bill, "bankReportedPaid"))); // mas o Saldo360 não registrou saída (não há conta integrada)
        Assert.Equal(["BillPaymentWithoutBankDebit"], Reasons(await ReviewAsync(c)));
        await using var db = Db();
        Assert.Equal(0, await db.FinancialCreditCardInvoicePayments.CountAsync());
    }

    [Fact]
    public async Task ABankDebitAlreadyBookedAsAPlainExpenseIsReclassifiedWhenTheCardAndItsBillArrive()
    {
        var c = await ArrangeAsync("t932-reclassify", seed: false);
        Pluggy.TransactionsByAccount[BankA] = [BankPayment("pay", 10, 230m)];
        await LinkBankAsync(c);
        await SyncAsync(c);
        var before = Assert.Single(await CoreAsync(c.Workspace));
        Assert.Equal(FinancialTransactionOrigin.PluggyImport, before.Origin); // até aqui é uma despesa simples (rótulo estruturado: sem categoria de consumo)

        SeedCycle(230m, payments: [(230m, 10)]);
        var cardLink = await LinkCardAsync(c, 3, 12);
        await SyncAsync(c);

        var after = Assert.Single(await CoreAsync(c.Workspace));
        Assert.Equal((FinancialTransactionOrigin.CreditCardInvoicePayment, 230m), (after.Origin, after.Amount));
        Assert.True((await CoreAsync(c.Workspace, includeCancelled: true)).Single(t => t.Id == before.Id).IsCancelled); // a despesa simples foi substituída, não somada
        Assert.Equal("Paid", (await BillsOfAsync(c, cardLink))[0].GetProperty("payment").GetString());
        await PostAsync(c, $"{Integration(c.Workspace)}/reprocess");
        Assert.Single(await CoreAsync(c.Workspace));
    }

    // ---- 13 a 15. Âncora de saldo -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheBankBalanceIsAReferenceThatEnablesTheBalanceWithoutManualOpeningAndOnlyOverTheConsistentInterval()
    {
        var c = await ArrangeAsync("t932-anchor");
        Pluggy.AccountsByItem[ItemA] = [FakePluggyClient.Account(BankA, ItemA) with { UpdatedAt = DateTimeOffset.UtcNow }, FakePluggyClient.Account(CardA, ItemA, "CREDIT", "5555444433332222")];
        var detail = await LinkBankAsync(c);
        var accountId = detail.GetProperty("link").GetProperty("financialAccountId").GetGuid();
        await SyncAsync(c);
        await using var db = Db();
        var movementsBefore = await db.FinancialTransactions.CountAsync();

        var balance = await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/accounts/{accountId}/balance");

        // Há um movimento postado em revisão em D-6: o intervalo calculável começa no dia seguinte e o saldo inicial derivado reproduz o observado.
        Assert.Equal(("1500.75", "BalanceAnchor", D(5)), (balance.GetProperty("balance").GetString(), balance.GetProperty("openingSource").GetString(), balance.GetProperty("openingBalanceDate").GetString()));
        var earlier = await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/accounts/{accountId}/balance?asOf={D(8)}");
        Assert.Equal(JsonValueKind.Null, earlier.GetProperty("balance").ValueKind); // antes do intervalo coberto: indisponível, nunca estimado
        Assert.Equal("BeforeOpeningBalanceDate", earlier.GetProperty("unavailableReason").GetString());
        var anchor = await db.FinancialAccountBalanceAnchors.AsNoTracking().SingleAsync();
        Assert.Equal((1500.75m, BalanceAnchorKind.CurrentBalance, "Pluggy", BalanceAnchorReliability.Provisional, "ExcludedMovementsLimitHistory", true),
            (anchor.Amount, anchor.Kind, anchor.Source, anchor.Reliability, anchor.UnavailableReason, anchor.IsCurrent));
        Assert.Equal(movementsBefore, await db.FinancialTransactions.CountAsync()); // a âncora não gera receita nem despesa
        Assert.Null((await db.FinancialAccounts.AsNoTracking().SingleAsync(a => a.Id == accountId)).OpeningBalance); // e não altera o cadastro da conta

        var posting = (await ReviewAsync(c)).GetProperty("items")[0].GetProperty("postingId").GetGuid();
        await PostAsync(c, $"{Integration(c.Workspace)}/review/{posting}/resolve", new { resolution = "AsExpense" });
        var wider = await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/accounts/{accountId}/balance");
        Assert.Equal(("1500.75", D(9)), (wider.GetProperty("balance").GetString(), wider.GetProperty("openingBalanceDate").GetString())); // resolvido o item, o histórico calculável se amplia
    }

    [Fact]
    public async Task TheOverdraftLimitIsNeverAddedToTheBalanceAndAContradictoryBalanceNatureIsNotUsed()
    {
        var c = await ArrangeAsync("t932-overdraft", seed: false);
        Pluggy.TransactionsByAccount[BankA] = [Tx("x", BankA, 3, -100m)];
        Pluggy.AccountsByItem[ItemA] = [FakePluggyClient.Account(BankA, ItemA, balance: -500m) with { UpdatedAt = DateTimeOffset.UtcNow, Bank = new PluggyBankData("0001", -500m, null, 1000m, 500m, null) },
            FakePluggyClient.Account(CardA, ItemA, "CREDIT", "5555444433332222")];
        var detail = await LinkBankAsync(c);
        var accountId = detail.GetProperty("link").GetProperty("financialAccountId").GetGuid();
        await SyncAsync(c);

        var balance = await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/accounts/{accountId}/balance");
        var view = (await JsonGetAsync(c, $"{Integration(c.Workspace)}/links/{detail.GetProperty("link").GetProperty("id").GetGuid()}"));

        Assert.Equal(("-500.00", "BalanceAnchor"), (balance.GetProperty("balance").GetString(), balance.GetProperty("openingSource").GetString())); // saldo negativo; o limite (1000) não é somado
        Assert.Equal("1000.00", view.GetProperty("link").GetProperty("observed").GetProperty("overdraftContractedLimit").GetString());
        Assert.Contains(view.GetProperty("reconciliation").GetProperty("notes").EnumerateArray().Select(n => n.GetString()), n => n!.Contains("cheque especial"));

        // Saldo positivo com cheque especial em uso é incoerente: a natureza do saldo é incerta e a referência não é usada.
        Pluggy.AccountsByItem[ItemA] = [FakePluggyClient.Account(BankA, ItemA, balance: 200m) with { UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(1), Bank = new PluggyBankData("0001", 200m, null, 1000m, 100m, null) },
            FakePluggyClient.Account(CardA, ItemA, "CREDIT", "5555444433332222")];
        await SyncAsync(c);
        var unusable = await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/accounts/{accountId}/balance");
        Assert.Equal(JsonValueKind.Null, unusable.GetProperty("balance").ValueKind);
        await using var db = Db();
        var current = await db.FinancialAccountBalanceAnchors.AsNoTracking().SingleAsync(a => a.IsCurrent);
        Assert.Equal((BalanceAnchorReliability.Unreliable, "BalanceNatureAmbiguous"), (current.Reliability, current.UnavailableReason));
        Assert.Equal(2, await db.FinancialAccountBalanceAnchors.CountAsync()); // o histórico de referências é mantido
    }

    // ---- 16. Fechamento e vencimento ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheUsualClosingAndDueDaysAreSuggestedFromTheBankHistoryAndOnlyChangedAfterConfirmation()
    {
        var c = await ArrangeAsync("t932-schedule", seed: false);
        // Fechamento no dia 8 e vencimento no dia 15 em quatro faturas passadas (fechamento e vencimento são datas diferentes e nunca se substituem).
        Pluggy.BillsByAccount[CardA] = [.. new[] { 3, 4, 5, 6 }.Select(m => FixedBill($"b{m}", new DateOnly(2026, m, 8), new DateOnly(2026, m, 15)))];
        var cardLink = await LinkCardAsync(c, 20, 25);
        var linkId = cardLink.GetProperty("link").GetProperty("id").GetGuid();
        await SyncAsync(c);
        var url = $"{Integration(c.Workspace)}/links/{linkId}";

        var card = (await JsonGetAsync(c, url)).GetProperty("reconciliation").GetProperty("card");
        Assert.Equal((20, 25, 8, 15), (card.GetProperty("currentClosingDay").GetInt32(), card.GetProperty("currentDueDay").GetInt32(), card.GetProperty("suggestedClosingDay").GetInt32(), card.GetProperty("suggestedDueDay").GetInt32()));
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(c, $"{url}/card-schedule-suggestion", new { })).StatusCode); // sem confirmação não altera
        await using (var db = Db()) { var row = await db.FinancialCreditCards.AsNoTracking().SingleAsync(); Assert.Equal((20, 25), (row.ClosingDay, row.DueDay)); }

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(c, $"{url}/card-schedule-suggestion", new { confirm = true })).StatusCode);

        await using var after = Db();
        var updated = await after.FinancialCreditCards.AsNoTracking().SingleAsync();
        Assert.Equal((8, 15), (updated.ClosingDay, updated.DueDay));
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(c, $"{url}/card-schedule-suggestion", new { confirm = true })).StatusCode); // idempotente
    }

    [Fact]
    public async Task AnInconsistentBillHistoryNeverProducesASuggestion()
    {
        var c = await ArrangeAsync("t932-schedule-inconsistent", seed: false);
        Pluggy.BillsByAccount[CardA] = [.. new[] { 3, 4, 5, 6 }.Select(m => FixedBill($"b{m}", new DateOnly(2026, m, m * 4), new DateOnly(2026, m, m * 4 + 3)))]; // dias 12, 16, 20 e 24
        var cardLink = await LinkCardAsync(c, 20, 25);
        await SyncAsync(c);
        var url = $"{Integration(c.Workspace)}/links/{cardLink.GetProperty("link").GetProperty("id").GetGuid()}";

        var card = (await JsonGetAsync(c, url)).GetProperty("reconciliation").GetProperty("card");
        Assert.Equal(JsonValueKind.Null, card.GetProperty("suggestedClosingDay").ValueKind);
        Assert.Equal("Inconsistent", card.GetProperty("closingScheduleReason").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(c, $"{url}/card-schedule-suggestion", new { confirm = true })).StatusCode);
    }

    // ---- 17 a 20. Reprocessamento, categorias, transferências e consistência -----------------------------------------------------

    [Fact]
    public async Task ReprocessingIsIdempotentPreservesManualCategoriesAndKeepsTransfersAndPaymentsStable()
    {
        var c = await ArrangeAsync("t932-reprocess", seed: false);
        var itemB = await LinkAsync(c.Client, c.Session, c.Workspace, ItemB, "Nubank");
        Pluggy.TransactionsByAccount[BankA] = [BankPayment("pay", 10, 230m), Tx("own-out", BankA, 6, -500m, operation: "PIX", description: "PIX ENVIADO", category: "Transfer - PIX"), Tx("shop", BankA, 5, -30m)];
        Pluggy.TransactionsByAccount[BankB] = [Tx("own-in", BankB, 6, 500m, operation: "PIX", description: "PIX RECEBIDO", category: "Transfer - PIX")];
        SeedCycle(230m, payments: [(230m, 10)]);
        await LinkBankAsync(c);
        await PostAsync(c, $"{Integration(c.Workspace)}/links", new { connectionId = itemB, pluggyAccountId = BankB, mode = "CreateNew", name = "Nubank", financialScope = "PF" });
        var cardLink = await LinkCardAsync(c, 3, 12);
        await SyncAsync(c);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(c, $"{Root(c.Workspace)}/connections/{itemB}/sync")).StatusCode);
        var manual = await CreateCategoryAsync(c, "Mercado manual", "Expense");
        var shop = (await CoreAsync(c.Workspace)).Single(t => t.Amount == 30m);
        Assert.Equal(HttpStatusCode.OK, (await Send(c.Client, HttpMethod.Put, $"/api/financial-workspaces/{c.Workspace}/transactions/{shop.Id}/category", c.Session, new { categoryId = manual })).StatusCode);
        string Snapshot()
        {
            using var db = Db();
            var parts = new List<string>();
            parts.AddRange(db.FinancialTransactions.AsNoTracking().Where(t => !t.IsCancelled).OrderBy(t => t.Id).ToList().Select(t => $"{t.Id}:{t.Amount}:{t.Origin}:{t.CategoryId}"));
            parts.AddRange(db.FinancialTransfers.AsNoTracking().Where(t => !t.IsCancelled).OrderBy(t => t.Id).ToList().Select(t => $"T{t.Id}:{t.Amount}"));
            parts.AddRange(db.FinancialCreditCardInvoicePayments.AsNoTracking().OrderBy(p => p.Id).ToList().Select(p => $"P{p.Id}:{p.Amount}:{p.Status}"));
            parts.AddRange(db.FinancialCreditCardInvoices.AsNoTracking().OrderBy(i => i.Id).ToList().Select(i => $"I{i.Id}:{i.InstitutionTotal}"));
            return string.Join('|', parts);
        }
        var before = Snapshot();

        var first = await JsonOf(await PostAsync(c, $"{Integration(c.Workspace)}/reprocess"));
        var second = await JsonOf(await PostAsync(c, $"{Integration(c.Workspace)}/reprocess"));

        Assert.Equal(before, Snapshot()); // nada duplicado, nada desfeito
        Assert.Equal((0, 0, 0, 0), (first.GetProperty("created").GetInt32(), first.GetProperty("updated").GetInt32(), second.GetProperty("created").GetInt32(), second.GetProperty("purchasesRehomed").GetInt32()));
        await using var check = Db();
        Assert.Equal(manual, (await check.FinancialTransactions.AsNoTracking().SingleAsync(t => t.Id == shop.Id)).CategoryId); // categoria manual preservada
        Assert.Equal(1, await check.FinancialTransfers.CountAsync(t => !t.IsCancelled)); // transferência PF/PJ preservada
        Assert.Equal(1, await check.FinancialCreditCardInvoicePayments.CountAsync());
        Assert.Equal("Paid", (await BillsOfAsync(c, cardLink))[0].GetProperty("payment").GetString());
    }

    [Fact]
    public async Task DashboardAndAgendaStayConsistentWithPaymentsCreditsAndAnchoredBalances()
    {
        var c = await ArrangeAsync("t932-consistency", seed: false);
        Pluggy.AccountsByItem[ItemA] = [FakePluggyClient.Account(BankA, ItemA, balance: 1000m) with { UpdatedAt = DateTimeOffset.UtcNow }, FakePluggyClient.Account(CardA, ItemA, "CREDIT", "5555444433332222")];
        Pluggy.TransactionsByAccount[BankA] = [BankPayment("pay", 10, 190m), Tx("salary", BankA, 12, 1200m, operation: "OUTROS", description: "SALARIO", category: "Salary")];
        SeedCycle(190m, payments: [(190m, 10)], extra: [Tx("refund", CardA, 12, 40m, operation: "ESTORNO", description: "ESTORNO", card: OnBill("bill-1"), type: "CREDIT", category: "Shopping")]);
        var bank = await LinkBankAsync(c);
        await LinkCardAsync(c, 3, 12);
        await SyncAsync(c);

        var from = Today.AddDays(-40); var through = Today;
        var calendar = await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/financial-calendar?from={from:yyyy-MM-dd}&through={through:yyyy-MM-dd}");
        var summary = calendar.GetProperty("summary");
        // Caixa: o salário entra e só o pagamento da fatura (190) sai; compras (230) e o estorno (40) ficam no cartão e não viram receita nem despesa bancária.
        Assert.Equal(("1200.00", "190.00"), (summary.GetProperty("realizedIncome").GetString(), summary.GetProperty("realizedExpense").GetString()));
        var items = calendar.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        var dashboard = await JsonGetAsync(c, $"/api/financial-workspaces/{c.Workspace}/financial-dashboard?month={Today:yyyy-MM}");
        Assert.Equal("1000.00", dashboard.GetProperty("position").GetProperty("realizedBalance").GetString()); // o saldo da conta vem da referência bancária
        Assert.Equal(1000m, (await Db().FinancialAccountBalanceAnchors.AsNoTracking().SingleAsync(a => a.IsCurrent)).Amount);
    }
}
