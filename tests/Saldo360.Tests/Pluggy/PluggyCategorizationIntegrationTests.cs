using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Classification;
using Saldo360.Application.Pluggy;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Pluggy;
using Saldo360.Infrastructure.Pluggy;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Pluggy;

/// <summary>
/// T09.3.1: categorização híbrida (manual → regras → categoria Pluggy → IA) dos lançamentos importados. API real sobre PostgreSQL; a Pluggy e o
/// fallback de IA são simulados: nenhuma chamada externa. Valores, datas e saldos dos lançamentos nunca podem mudar.
/// </summary>
[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Pluggy")]
[Trait("Dependency", "PostgreSQL")]
public sealed class PluggyCategorizationIntegrationTests(PostgresFixture postgres) : PluggyApiTestBase(postgres)
{
    private static readonly DateOnly Today = BusinessCalendar.Today(DateTimeOffset.UtcNow);
    private static string D(int daysAgo) => Today.AddDays(-daysAgo).ToString("yyyy-MM-dd");

    private sealed record Ctx(string Session, Guid Workspace, HttpClient Client, Guid Connection);

    private static PluggyTransaction Tx(string id, string account, int daysAgo, decimal amount, string? operation = "CARTAO", string description = "Mercado", string? category = "Groceries",
        PluggyCardMetadata? card = null, string? type = null) =>
        FakePluggyClient.Transaction(id, account, D(daysAgo), amount, "POSTED", card) with
        {
            OperationType = operation, Description = description, DescriptionRaw = description.ToUpperInvariant(), Type = type ?? (amount < 0 ? "DEBIT" : "CREDIT"), Category = category,
        };

    private static string Integration(Guid workspace) => $"/api/financial-workspaces/{workspace}/pluggy-integration";

    private async Task<Ctx> ArrangeAsync(string user, bool link = true)
    {
        var session = await SignInAsync(user);
        var workspace = await CreateWorkspaceAsync(session);
        var client = LabClient();
        var connection = await LinkAsync(client, session, workspace.Id, ItemA, "Itaú PF");
        Pluggy.TransactionPageSize = 50;
        Pluggy.TransactionsByAccount[BankA] =
        [
            Tx("g1", BankA, 9, -10.25m, description: "Mercado Central"),
            Tx("g2", BankA, 8, -20m, description: "Mercado Bairro"),
            Tx("sal", BankA, 7, 1500m, "OUTROS", "Empresa X Pagamento", "Salary"),
            Tx("cc", BankA, 6, -50m, "BOLETO", "Debito automatico X", "Credit card payment"),
            Tx("sp", BankA, 5, -30m, "OUTROS", "Entre contas", "Same person transfer"),
            Tx("unk", BankA, 4, -15m, "CARTAO", "Loja Zeta", "Mystery Category"),
            Tx("pix", BankA, 3, -40m, "PIX", "PIX ENVIADO MARIA SOUZA", "Transfer - PIX"),
        ];
        var ctx = new Ctx(session, workspace.Id, client, connection);
        if (link) await LinkBankAsync(ctx);
        return ctx;
    }

    private Task<HttpResponseMessage> PostAsync(Ctx c, string url, object? body = null) => Send(c.Client, HttpMethod.Post, url, c.Session, body ?? new { });

    private async Task<JsonElement> LinkBankAsync(Ctx c)
    {
        var response = await PostAsync(c, $"{Integration(c.Workspace)}/links", new { connectionId = c.Connection, pluggyAccountId = BankA, mode = "CreateNew", name = "Itaú corrente", financialScope = "PF" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await JsonOf(response);
    }

    private async Task SyncAsync(Ctx c)
    {
        var response = await PostAsync(c, $"{Root(c.Workspace)}/connections/{c.Connection}/sync");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        Assert.Equal(0, (await JsonOf(response)).GetProperty("errors").GetInt32());
    }

    private async Task<Guid> CreateCategoryAsync(Ctx c, string name, string type)
    {
        var response = await Send(c.Client, HttpMethod.Post, $"/api/financial-workspaces/{c.Workspace}/categories", c.Session, new { name, type });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await JsonOf(response)).GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> RunAsync(Ctx c, object? body = null)
    {
        var response = await PostAsync(c, $"{Integration(c.Workspace)}/categorization/run", body ?? new { });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await JsonOf(response);
    }

    /// <summary>Retrato do que NUNCA pode mudar: valor, data, tipo, conta e descrição dos lançamentos ativos.</summary>
    private async Task<string[]> MoneySnapshotAsync(Guid workspace)
    {
        await using var db = Db();
        return (await db.FinancialTransactions.AsNoTracking().Where(t => t.WorkspaceId == workspace && !t.IsCancelled).ToListAsync())
            .Select(t => $"{t.Description}|{t.Type}|{t.Amount}|{t.TransactionDate}|{t.AccountId}").Order().ToArray();
    }

    private async Task<Dictionary<string, (string? Category, PluggyCategorySource Source)>> CategoriesByImportedAsync(Guid workspace)
    {
        await using var db = Db();
        var rows = await (from p in db.PluggyPostings.AsNoTracking()
                          join i in db.PluggyImportedTransactions.AsNoTracking() on p.ImportedTransactionId equals i.Id
                          where p.WorkspaceId == workspace && p.Kind == PluggyPostingKind.BankMovement
                          select new { i.PluggyTransactionId, p.FinancialTransactionId, p.CategorySource }).ToListAsync();
        var result = new Dictionary<string, (string?, PluggyCategorySource)>();
        foreach (var r in rows)
        {
            var name = await db.FinancialTransactions.AsNoTracking().Where(t => t.Id == r.FinancialTransactionId)
                .Select(t => db.FinancialCategories.Where(c => c.Id == t.CategoryId).Select(c => c.Name).FirstOrDefault()).SingleAsync();
            result[r.PluggyTransactionId] = (name, r.CategorySource);
        }
        return result;
    }

    // ---- Local: regras + categoria Pluggy + criação controlada + retroativo ---------------------------------------------------

    [Fact]
    public async Task RetroactiveLocalRunMapsPluggyCategoriesReusesEquivalentOnesCreatesCanonicalOnesAndNeverTouchesMoney()
    {
        var c = await ArrangeAsync("t931-local");
        await SyncAsync(c);
        var existingFood = await CreateCategoryAsync(c, "Alimentacao ", "Expense"); // equivalente a "Alimentação" (acento, espaço)
        var before = await MoneySnapshotAsync(c.Workspace);
        Assert.All((await CategoriesByImportedAsync(c.Workspace)).Values, v => Assert.Null(v.Category)); // nada categorizado ainda

        var result = await RunAsync(c);

        var categories = await CategoriesByImportedAsync(c.Workspace);
        Assert.Equal(("Alimentacao", PluggyCategorySource.Pluggy), categories["g1"]);
        Assert.Equal(("Alimentacao", PluggyCategorySource.Pluggy), categories["g2"]);
        Assert.Equal(("Salário", PluggyCategorySource.Pluggy), categories["sal"]);
        // Movimentos especiais: decididos por regra, sem categoria de consumo.
        Assert.Equal((null, PluggyCategorySource.None), categories["cc"]);
        Assert.Equal((null, PluggyCategorySource.None), categories["sp"]);
        // Sem evidência estruturada nem IA: ficam sem categoria, para revisão.
        Assert.Equal((null, PluggyCategorySource.None), categories["unk"]);
        Assert.Equal((null, PluggyCategorySource.None), categories["pix"]);
        await using (var db = Db())
        {
            var names = await db.FinancialCategories.AsNoTracking().Where(x => x.WorkspaceId == c.Workspace).Select(x => x.Name).OrderBy(x => x).ToListAsync();
            Assert.Equal(["Alimentacao", "Salário"], names); // "Alimentação" não foi duplicada; só a ausente foi criada
            Assert.Equal(existingFood, (await db.FinancialCategories.AsNoTracking().SingleAsync(x => x.Name == "Alimentacao")).Id);
        }
        Assert.Equal(before, await MoneySnapshotAsync(c.Workspace)); // valores, datas, contas e saldos intactos
        // As regras (pagamento de fatura, transferência própria, PIX a terceiros) já foram aplicadas pela sincronização ao criar cada lançamento.
        await using (var rules = Db())
        {
            var ruled = await (from p in rules.PluggyPostings.AsNoTracking() join i in rules.PluggyImportedTransactions on p.ImportedTransactionId equals i.Id
                               where new[] { "cc", "sp", "pix" }.Contains(i.PluggyTransactionId) select new { p.CategoryMethodVersion, p.CategoryClassifiedAt }).ToListAsync();
            Assert.Equal(3, ruled.Count(r => r.CategoryMethodVersion!.StartsWith("rule:", StringComparison.Ordinal) && r.CategoryClassifiedAt != null));
        }
        Assert.Equal((3, 0, 1, 1), (result.GetProperty("pluggy").GetInt32(), result.GetProperty("rule").GetInt32(), result.GetProperty("categoriesCreated").GetInt32(), result.GetProperty("unresolved").GetInt32()));
        Assert.Equal(0, Ai.Calls); // etapa local nunca consulta a IA

        // A API de lançamentos expõe a origem da categoria para a tela: Pluggy para os mapeados, nenhuma para o que ficou sem categoria.
        var page = await JsonOf(await Send(c.Client, HttpMethod.Get, $"/api/financial-workspaces/{c.Workspace}/transactions?pageSize=50", c.Session));
        var items = page.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count(i => i.GetProperty("categorySource").GetString() == "Pluggy" && i.GetProperty("category").ValueKind == JsonValueKind.Object && i.GetProperty("type").GetString() == "Expense"));
        Assert.Contains(items, i => i.GetProperty("category").ValueKind == JsonValueKind.Null && i.GetProperty("categorySource").GetString() == "None" && i.GetProperty("categoryNeedsReview").GetBoolean());
    }

    [Fact]
    public async Task ReRunningIsIdempotentAndDoesNotReprocessWhatWasAlreadyClassified()
    {
        var c = await ArrangeAsync("t931-idem");
        await SyncAsync(c);
        await RunAsync(c);
        var before = await MoneySnapshotAsync(c.Workspace);
        await using var db = Db();
        var stamps = await db.PluggyPostings.AsNoTracking().Select(p => new { p.Id, p.CategoryClassifiedAt }).ToDictionaryAsync(p => p.Id, p => p.CategoryClassifiedAt);

        var second = await RunAsync(c);

        Assert.Equal((0, 0, 0, 0, 0), (second.GetProperty("pluggy").GetInt32(), second.GetProperty("rule").GetInt32(), second.GetProperty("ai").GetInt32(), second.GetProperty("categoriesCreated").GetInt32(), second.GetProperty("unresolved").GetInt32()));
        Assert.Equal(before, await MoneySnapshotAsync(c.Workspace));
        await using var db2 = Db();
        Assert.Equal(stamps, await db2.PluggyPostings.AsNoTracking().Select(p => new { p.Id, p.CategoryClassifiedAt }).ToDictionaryAsync(p => p.Id, p => p.CategoryClassifiedAt));
        Assert.Equal(2, await db2.FinancialCategories.CountAsync(x => x.WorkspaceId == c.Workspace)); // nenhuma categoria nova na segunda execução
    }

    [Fact]
    public async Task ManualCategoryIsPreservedByTheRunAndBySubsequentSyncs()
    {
        var c = await ArrangeAsync("t931-manual");
        await SyncAsync(c);
        var leisure = await CreateCategoryAsync(c, "Lazer", "Expense");
        await using (var db = Db())
        {
            var transactionId = await (from p in db.PluggyPostings join i in db.PluggyImportedTransactions on p.ImportedTransactionId equals i.Id
                                       where i.PluggyTransactionId == "g1" select p.FinancialTransactionId).SingleAsync();
            var response = await Send(c.Client, HttpMethod.Put, $"/api/financial-workspaces/{c.Workspace}/transactions/{transactionId}/category", c.Session, new { categoryId = leisure });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var run = await RunAsync(c);
        await SyncAsync(c);
        await RunAsync(c, new { force = true }); // até a execução forçada respeita a escolha do usuário

        var categories = await CategoriesByImportedAsync(c.Workspace);
        Assert.Equal(("Lazer", PluggyCategorySource.User), categories["g1"]);
        Assert.Equal(1, run.GetProperty("manual").GetInt32());
        Assert.Equal(PluggyCategorySource.Pluggy, categories["g2"].Source);
    }

    [Fact]
    public async Task ManualSubcategoryAndExpenseNatureSurviveRepeatedSyncsForcedRunsAndTheAi_AndNothingIsInferredAutomatically()
    {
        var c = await ArrangeAsync("r02-manual-nature");
        await SyncAsync(c);
        var housing = await CreateCategoryAsync(c, "Moradia", "Expense");
        var response = await Send(c.Client, HttpMethod.Post, $"/api/financial-workspaces/{c.Workspace}/categories", c.Session, new { name = "Condomínio", parentCategoryId = housing });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var sub = (await JsonOf(response)).GetProperty("id").GetGuid();
        Guid transactionId;
        await using (var db = Db())
        {
            transactionId = await (from p in db.PluggyPostings join i in db.PluggyImportedTransactions on p.ImportedTransactionId equals i.Id
                                   where i.PluggyTransactionId == "g1" select p.FinancialTransactionId!.Value).SingleAsync();
            // Antes de qualquer decisão do usuário, nada é inferido: tudo que veio do banco nasce "Não definida".
            Assert.All(await db.FinancialTransactions.AsNoTracking().Where(t => t.WorkspaceId == c.Workspace).ToListAsync(), t => Assert.Equal(ExpenseNature.Undefined, t.Nature));
        }
        var url = $"/api/financial-workspaces/{c.Workspace}/transactions/{transactionId}";
        Assert.Equal(HttpStatusCode.OK, (await Send(c.Client, HttpMethod.Put, $"{url}/category", c.Session, new { categoryId = sub })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(c.Client, HttpMethod.Put, $"{url}/expense-nature", c.Session, new { expenseNature = "Fixed" })).StatusCode);
        Ai.Available = true;
        Ai.Responder = FakeFinancialClassificationModel.Always("Moradia");

        await RunAsync(c);
        await SyncAsync(c);
        await RunAsync(c, new { force = true });
        await RunAsync(c, new { useAi = true, confirmExternal = true, force = true });

        await using var verify = Db();
        var stored = await verify.FinancialTransactions.AsNoTracking().SingleAsync(t => t.Id == transactionId);
        Assert.Equal((sub, ExpenseNature.Fixed), (stored.CategoryId, stored.Nature));
        Assert.Equal(PluggyCategorySource.User, (await CategoriesByImportedAsync(c.Workspace))["g1"].Source);
        var unmapped = await (from p in verify.PluggyPostings join i in verify.PluggyImportedTransactions on p.ImportedTransactionId equals i.Id
                              where i.PluggyTransactionId == "unk" select p.FinancialTransactionId!.Value).SingleAsync();
        var unmappedTransaction = await verify.FinancialTransactions.AsNoTracking().SingleAsync(t => t.Id == unmapped);
        Assert.Equal(ExpenseNature.Undefined, unmappedTransaction.Nature); // categoria externa desconhecida: continua sem natureza inferida
    }

    [Fact]
    public async Task ThePluggyCategoryMapsToAnExistingSubcategory_AndASubcategoryWhoseMainIsInactiveIsNeverApplied()
    {
        var c = await ArrangeAsync("r02-sub-mapping");
        await SyncAsync(c);
        var general = await CreateCategoryAsync(c, "Despesas gerais", "Expense");
        var response = await Send(c.Client, HttpMethod.Post, $"/api/financial-workspaces/{c.Workspace}/categories", c.Session, new { name = "Alimentação", parentCategoryId = general });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var sub = (await JsonOf(response)).GetProperty("id").GetGuid();

        var run = await RunAsync(c);

        var categories = await CategoriesByImportedAsync(c.Workspace);
        Assert.Equal(("Alimentação", PluggyCategorySource.Pluggy), categories["g1"]);
        await using var db = Db();
        Assert.Equal(1, await db.FinancialCategories.CountAsync(x => x.WorkspaceId == c.Workspace && x.Name == "Alimentação")); // nenhuma principal homônima foi criada
        Assert.Equal(sub, await db.FinancialTransactions.AsNoTracking().Where(t => t.WorkspaceId == c.Workspace && t.Description.Contains("Mercado Central")).Select(t => t.CategoryId!.Value).SingleAsync());

        Assert.Equal(HttpStatusCode.OK, (await Send(c.Client, HttpMethod.Put, $"/api/financial-workspaces/{c.Workspace}/categories/{general}", c.Session, new { name = "Despesas gerais", isActive = false })).StatusCode);
        var posting = await (from p in db.PluggyPostings join i in db.PluggyImportedTransactions on p.ImportedTransactionId equals i.Id
                             where p.WorkspaceId == c.Workspace && i.PluggyTransactionId == "g2" select p).AsNoTracking().SingleAsync();
        var current = await db.FinancialTransactions.AsNoTracking().Where(t => t.Id == posting.FinancialTransactionId).Select(t => t.CategoryId).SingleAsync();
        var ownerId = await db.FinancialWorkspaces.AsNoTracking().Where(w => w.Id == c.Workspace).Select(w => w.OwnerUserId).SingleAsync();
        var applied = await new PluggyCategorizationRepository(db).ApplyAsync(c.Workspace, ownerId, [new(posting.Id, sub, null, null, PluggyCategorySource.AI, "ai:test", "x", false, current)], DateTimeOffset.UtcNow, default);
        Assert.Equal((0, 1), (applied.Applied, applied.SkippedChanged)); // subcategoria de principal inativa: indisponível para nova classificação
        Assert.True(run.GetProperty("pluggy").GetInt32() >= 1);
    }

    [Fact]
    public async Task NewImportedMovementsAreClassifiedLocallyDuringSyncWithoutTheModel()
    {
        var c = await ArrangeAsync("t931-sync");
        await SyncAsync(c);
        await RunAsync(c); // cria "Alimentação" e "Salário"
        Pluggy.TransactionsByAccount[BankA] = [.. Pluggy.TransactionsByAccount[BankA], Tx("g3", BankA, 1, -7m, description: "Padaria Nova"), Tx("pay", BankA, 2, -9m, "BOLETO", "Debito automatico Y", "Credit card payment")];

        await SyncAsync(c);

        var categories = await CategoriesByImportedAsync(c.Workspace);
        Assert.Equal(("Alimentação", PluggyCategorySource.Pluggy), categories["g3"]);
        Assert.Equal((null, PluggyCategorySource.None), categories["pay"]);
        Assert.Equal(0, Ai.Calls); // sincronização nunca depende nem aciona a IA
    }

    private async Task<Ctx> ArrangeCardAsync(string user, decimal billTotal, bool withBill = true)
    {
        var c = await ArrangeAsync(user, link: false);
        Pluggy.TransactionsByAccount[BankA] = [];
        Pluggy.BillsByAccount[CardA] = withBill ? [new("bill-1", DateTimeOffset.Parse(D(8) + "T00:00:00Z"), billTotal, "BRL", null, null, [], DateTimeOffset.Parse(D(15) + "T00:00:00Z"), [])] : [];
        var billId = withBill ? "bill-1" : null;
        Pluggy.TransactionsByAccount[CardA] =
        [
            Tx("c1", CardA, 20, 150m, "PAGAMENTO", "Restaurante", "Eating out", new(null, null, null, null, billId, "2222", null, null), "DEBIT"),
            Tx("c2", CardA, 19, 50m, "PAGAMENTO", "Coisa", "Strange", new(null, null, null, null, billId, "2222", null, null), "DEBIT"),
        ];
        var response = await PostAsync(c, $"{Integration(c.Workspace)}/links", new { connectionId = c.Connection, pluggyAccountId = CardA, mode = "CreateNew", name = "Cartão", closingDay = 10, dueDay = 18 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await SyncAsync(c);
        return c;
    }

    private async Task<string[]> PurchaseCategoriesAsync()
    {
        await using var db = Db();
        var purchases = await db.FinancialCreditCardPurchases.AsNoTracking().OrderBy(p => p.TotalAmount).ToListAsync();
        return purchases.Select(p => $"{p.TotalAmount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}:{db.FinancialCategories.Single(x => x.Id == p.CategoryId).Name}").ToArray();
    }

    [Fact]
    public async Task CardPurchasesLeaveTheFallbackCategoryWhenThePluggyCategoryIsKnownAndKeepAmounts()
    {
        var c = await ArrangeCardAsync("t931-card", billTotal: 999m); // total diferente: a fatura continua aberta
        Assert.Equal(["50.00:Outros", "150.00:Outros"], await PurchaseCategoriesAsync());

        var run = await RunAsync(c);

        Assert.Equal(["50.00:Outros", "150.00:Restaurantes"], await PurchaseCategoriesAsync()); // reconhecida muda; desconhecida mantém "Outros"; valores intactos
        Assert.Equal(1, run.GetProperty("pluggy").GetInt32());
    }

    /// <summary>Cria, para cada mês de vencimento de fatura, o orçamento com a meta "Todos os cartões" e soma o consumo, os registros de competência estimada e os itens.</summary>
    private async Task<(decimal Consumed, int Estimated, List<JsonElement> Items)> CardBudgetAsync(Ctx c)
    {
        List<DateOnly> dues;
        await using (var db = Db()) dues = await db.FinancialCreditCardInvoices.Where(i => i.WorkspaceId == c.Workspace).Select(i => i.DueDate).ToListAsync();
        var root = $"/api/financial-workspaces/{c.Workspace}/budgets";
        decimal consumed = 0m; var estimated = 0; var items = new List<JsonElement>();
        foreach (var competence in dues.Select(d => d.ToString("yyyy-MM")).Distinct())
        {
            var budget = await JsonOf(await Send(c.Client, HttpMethod.Post, root, c.Session, new { competence }));
            var budgetId = budget.GetProperty("id").GetGuid();
            var withTarget = await JsonOf(await Send(c.Client, HttpMethod.Post, $"{root}/{budgetId}/targets", c.Session, new { groupingType = "AllCards", plannedAmount = "1000.00" }));
            var targetId = withTarget.GetProperty("targets")[0].GetProperty("id").GetGuid();
            var execution = await JsonOf(await Send(c.Client, HttpMethod.Get, $"{root}/{budgetId}/execution", c.Session));
            var target = execution.GetProperty("targets").EnumerateArray().Single();
            consumed += decimal.Parse(target.GetProperty("consumed").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            estimated += target.GetProperty("coverage").GetProperty("estimatedCompetenceCount").GetInt32();
            var composition = await JsonOf(await Send(c.Client, HttpMethod.Get, $"{root}/{budgetId}/targets/{targetId}/composition", c.Session));
            items.AddRange(composition.GetProperty("items").EnumerateArray().Select(i => i.Clone()));
        }
        return (consumed, estimated, items);
    }

    [Fact]
    public async Task CardPurchasesWithTheBanksBillHaveAReliableCompetenceAndAreCountedInTheInvoiceDueMonth()
    {
        var c = await ArrangeCardAsync("r03-card-bill", billTotal: 999m);

        var (consumed, estimated, items) = await CardBudgetAsync(c);

        Assert.Equal((200m, 0, 2), (consumed, estimated, items.Count));
        Assert.All(items, item => Assert.True(item.GetProperty("competenceReliable").GetBoolean()));
    }

    [Fact]
    public async Task CardPurchasesWithoutTheBanksBillAreFlaggedAsEstimatedCompetenceInsteadOfSilentlyPlacedInTheCurrentMonth()
    {
        var c = await ArrangeCardAsync("r03-card-nobill", billTotal: 999m, withBill: false);

        var (consumed, estimated, items) = await CardBudgetAsync(c);

        // A compra entra pela competência do calendário configurado do cartão (mês de vencimento da fatura), nunca "no mês corrente" por omissão, e a meta avisa
        // que a cobertura é incompleta; os registros afetados são identificáveis na composição.
        Assert.Equal((200m, 2, 2), (consumed, estimated, items.Count));
        Assert.All(items, item => Assert.False(item.GetProperty("competenceReliable").GetBoolean()));
        Assert.All(items, item => Assert.False(string.IsNullOrEmpty(item.GetProperty("invoiceCompetence").GetString())));
    }

    [Fact]
    public async Task PurchasesOfAnInvoiceClosedByTheBankStayEditableSoTheirCategoryCanBeApplied()
    {
        var c = await ArrangeCardAsync("t931-card-closed", billTotal: 200m); // o banco informou o fechamento (total 200); as compras somam 200
        await using (var db = Db()) Assert.Equal(1, await db.FinancialCreditCardInvoices.CountAsync(i => i.InstitutionClosedAt != null && i.OfficialVersion == 0));

        await RunAsync(c);

        Assert.Equal(["50.00:Outros", "150.00:Restaurantes"], await PurchaseCategoriesAsync());
    }

    [Fact]
    public async Task PurchasesOfAnOfficiallyClosedInvoiceAreImmutableAndSkippedWithoutFailing()
    {
        var c = await ArrangeCardAsync("t931-card-official", billTotal: 999m);
        // Fatura oficial (consolidada por extrato com documento) torna as compras imutáveis no banco; a categorização as ignora sem falhar.
        await using (var db = Db())
        {
            var invoice = await db.FinancialCreditCardInvoices.SingleAsync(i => i.InstitutionClosedAt != null);
            invoice.CloseOfficial(1, 200m, DateTimeOffset.UtcNow);
            db.FinancialCreditCardInvoiceConsolidations.Add(FinancialCreditCardInvoiceConsolidation.Create(invoice.WorkspaceId, invoice.CreditCardId, invoice.Id, null,
                new string('a', 64), new string('b', 64), new string('c', 64), "{}", 1, 200m, await db.Users.Select(u => u.Id).FirstAsync(), DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        await RunAsync(c);

        Assert.Equal(["50.00:Outros", "150.00:Outros"], await PurchaseCategoriesAsync());
    }

    // ---- IA: confirmação, fallback, falhas -----------------------------------------------------------------------------------

    [Fact]
    public async Task UsingAiRequiresExplicitConfirmationAndNeverCallsTheModelWithout()
    {
        var c = await ArrangeAsync("t931-confirm");
        await SyncAsync(c);
        Ai.Available = true;

        var response = await PostAsync(c, $"{Integration(c.Workspace)}/categorization/run", new { useAi = true });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, Ai.Calls);
    }

    [Fact]
    public async Task AiIsOnlyTheFallbackAndSendsNothingPersonalOrFinancialAboutTheLocallyDecidedItems()
    {
        var c = await ArrangeAsync("t931-ai");
        await SyncAsync(c);
        await CreateCategoryAsync(c, "Lazer", "Expense");
        Ai.Available = true;
        Ai.Responder = FakeFinancialClassificationModel.Always("Lazer");

        var run = await RunAsync(c, new { useAi = true, confirmExternal = true });

        var categories = await CategoriesByImportedAsync(c.Workspace);
        Assert.Equal(("Lazer", PluggyCategorySource.AI), categories["unk"]);
        Assert.Equal(PluggyCategorySource.Pluggy, categories["g1"].Source); // a IA não toca o que a Pluggy/regras já decidiram
        Assert.Equal((null, PluggyCategorySource.None), categories["pix"]); // nome de pessoa: nunca enviado
        var sent = Ai.Requests.SelectMany(r => r.Items).ToList();
        Assert.Single(sent);
        Assert.DoesNotContain(sent, i => i.Text.Contains("MARIA", StringComparison.OrdinalIgnoreCase) || i.Text.Contains("PIX", StringComparison.OrdinalIgnoreCase));
        Assert.Equal((1, 1), (run.GetProperty("ai").GetInt32(), run.GetProperty("aiItemsSent").GetInt32()));
        Assert.True(run.GetProperty("inputTokens").GetInt32() > 0 && run.GetProperty("estimatedCostUsd").GetDecimal() > 0m);
        Assert.Equal("fake-model", run.GetProperty("model").GetString());

        var calls = Ai.Calls;
        await RunAsync(c, new { useAi = true, confirmExternal = true });
        Assert.Equal(calls, Ai.Calls); // idempotente: nada novo é reenviado
    }

    [Fact]
    public async Task AnAiSuggestionOfANewCategoryIsNotARegistrationAndTheMovementStaysInReviewWithoutCreatingAnything()
    {
        var c = await ArrangeAsync("t931-ai-suggest");
        await SyncAsync(c);
        Ai.Available = true;
        Ai.Responder = FakeFinancialClassificationModel.Suggest("Entretenimento");
        await using var before = Db();
        var categoriesBefore = await before.FinancialCategories.CountAsync(x => x.WorkspaceId == c.Workspace);

        var run = await RunAsync(c, new { useAi = true, confirmExternal = true });

        var categories = await CategoriesByImportedAsync(c.Workspace);
        Assert.Equal((null, PluggyCategorySource.None), categories["unk"]);
        Assert.Equal((0, 1), (run.GetProperty("ai").GetInt32(), run.GetProperty("aiItemsSent").GetInt32()));
        await using var db = Db();
        // Só a etapa do catálogo da Pluggy (inequívoco, determinístico) pode ter criado categorias canônicas; nenhuma nasce da sugestão da IA.
        var categoriesAfter = await db.FinancialCategories.CountAsync(x => x.WorkspaceId == c.Workspace);
        Assert.Equal(categoriesBefore + run.GetProperty("categoriesCreated").GetInt32(), categoriesAfter);
        Assert.Equal(0, await db.FinancialCategories.CountAsync(x => x.WorkspaceId == c.Workspace && x.Name == "Entretenimento"));
        var posting = await (from p in db.PluggyPostings join i in db.PluggyImportedTransactions on p.ImportedTransactionId equals i.Id where i.PluggyTransactionId == "unk" select p).AsNoTracking().SingleAsync();
        Assert.StartsWith("ai:", posting.CategoryMethodVersion);
        Assert.True(posting.CategoryNeedsReview);
        Assert.Contains("Entretenimento", posting.CategoryReason);

        var calls = Ai.Calls;
        await RunAsync(c, new { useAi = true, confirmExternal = true });
        Assert.Equal(calls, Ai.Calls); // idempotente: nada reenviado
        Assert.Equal(categoriesAfter, await db.FinancialCategories.CountAsync(x => x.WorkspaceId == c.Workspace));
    }

    [Fact]
    public async Task AProviderFailureKeepsLocalResultsDoesNotBreakTheSyncAndCanBeRetriedLater()
    {
        var c = await ArrangeAsync("t931-ai-fail");
        await SyncAsync(c);
        await CreateCategoryAsync(c, "Lazer", "Expense");
        Ai.Available = true;
        Ai.Failure = new FinancialClassificationModelException("ProviderAuthenticationFailed", false);

        var failed = await RunAsync(c, new { useAi = true, confirmExternal = true });

        var categories = await CategoriesByImportedAsync(c.Workspace);
        Assert.Equal(PluggyCategorySource.Pluggy, categories["g1"].Source); // etapa local preservada
        Assert.Equal((null, PluggyCategorySource.None), categories["unk"]);
        Assert.Equal("ProviderAuthenticationFailed", failed.GetProperty("aiFailure").GetString());
        await SyncAsync(c); // a sincronização segue funcionando

        Ai.Failure = null;
        Ai.Responder = FakeFinancialClassificationModel.Always("Lazer");
        await RunAsync(c, new { useAi = true, confirmExternal = true });
        Assert.Equal(("Lazer", PluggyCategorySource.AI), (await CategoriesByImportedAsync(c.Workspace))["unk"]);
    }

    [Fact]
    public async Task MissingCredentialMeansLocalOnlyAndAnExplicitUnavailableMarker()
    {
        var c = await ArrangeAsync("t931-ai-off");
        await SyncAsync(c);
        Ai.Available = false;

        var run = await RunAsync(c, new { useAi = true, confirmExternal = true });

        Assert.Equal(0, Ai.Calls);
        Assert.Equal("AiUnavailable", run.GetProperty("aiStoppedBecause").GetString());
        Assert.Equal(PluggyCategorySource.Pluggy, (await CategoriesByImportedAsync(c.Workspace))["g1"].Source); // a etapa local continua funcionando
        var overview = await JsonOf(await Send(c.Client, HttpMethod.Get, $"{Integration(c.Workspace)}/categorization", c.Session));
        Assert.False(overview.GetProperty("ai").GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task ACategoryFromAnotherWorkspaceOrOfTheWrongTypeIsNeverApplied()
    {
        var c = await ArrangeAsync("t931-isolation");
        await SyncAsync(c);
        var otherSession = await SignInAsync("t931-isolation-other");
        var other = new Ctx(otherSession, (await CreateWorkspaceAsync(otherSession)).Id, c.Client, Guid.Empty);
        var foreignCategory = await CreateCategoryAsync(other, "Alheia", "Expense");
        var incomeCategory = await CreateCategoryAsync(c, "Receita qualquer", "Income");
        await using var db = Db();
        var posting = await (from p in db.PluggyPostings join i in db.PluggyImportedTransactions on p.ImportedTransactionId equals i.Id
                             where p.WorkspaceId == c.Workspace && i.PluggyTransactionId == "g1" select p).AsNoTracking().SingleAsync();
        var repository = new PluggyCategorizationRepository(db);
        var now = DateTimeOffset.UtcNow;
        var ownerId = await db.FinancialWorkspaces.AsNoTracking().Where(w => w.Id == c.Workspace).Select(w => w.OwnerUserId).SingleAsync();

        var applied = await repository.ApplyAsync(c.Workspace, ownerId, [
            new(posting.Id, foreignCategory, null, null, PluggyCategorySource.AI, "ai:test", "x", false, null),
            new(posting.Id, incomeCategory, null, null, PluggyCategorySource.AI, "ai:test", "x", false, null),
        ], now, default);

        Assert.Equal((0, 2), (applied.Applied, applied.SkippedChanged));
        Assert.Null((await CategoriesByImportedAsync(c.Workspace))["g1"].Category);
    }

    [Fact]
    public async Task OnlyTheWorkspaceOwnerCanRunOrReadTheCategorization()
    {
        var c = await ArrangeAsync("t931-owner");
        var stranger = await SignInAsync("t931-stranger");

        var run = await Send(c.Client, HttpMethod.Post, $"{Integration(c.Workspace)}/categorization/run", stranger, new { });
        var overview = await Send(c.Client, HttpMethod.Get, $"{Integration(c.Workspace)}/categorization", stranger);
        var unknownField = await PostAsync(c, $"{Integration(c.Workspace)}/categorization/run", new { workspaceId = Guid.NewGuid() });

        Assert.Equal(HttpStatusCode.NotFound, run.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, overview.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknownField.StatusCode);
        Assert.Equal(0, Ai.Calls);
    }
}
