using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.Common;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>
/// R01: contas com saldo, compromissos da Agenda e metas da Visão Geral (<c>financial-dashboard/overview</c>), mais o filtro que mantém o pagamento de
/// fatura fora da lista de últimos lançamentos. PostgreSQL real, sem Pluggy nem OpenAI.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Dashboard")]
[Trait("Dependency", "PostgreSQL")]
public sealed class DashboardOverviewTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private static DateOnly Today => BusinessCalendar.Today(DateTimeOffset.UtcNow);

    private sealed record IdDto(Guid Id);
    private sealed record Setup(string Session, WorkspaceDto Workspace, Guid Pf, Guid Pj, Guid Reserve, Guid Income, Guid Expense)
    {
        public string Root => $"/api/financial-workspaces/{Workspace.Id}";
        public string Overview(string query = "") => $"{Root}/financial-dashboard/overview{query}";
    }

    private async Task<Setup> ArrangeAsync(string user)
    {
        var session = await SignInAsync(user);
        var workspace = await CreateWorkspaceAsync(session);
        var root = $"/api/financial-workspaces/{workspace.Id}";
        async Task<Guid> Create(string path, object body) => (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{root}/{path}", session, body))).Id;
        var opening = new DateOnly(Today.Year, Today.Month, 1).AddMonths(-1);
        var pf = await Create("accounts", new { name = "Conta PF", type = "Checking", financialScope = "PF", isPrimary = true, openingBalance = "1000.00", openingBalanceDate = opening });
        var pj = await Create("accounts", new { name = "Conta PJ", type = "Checking", financialScope = "PJ", openingBalance = "500.00", openingBalanceDate = opening });
        var reserve = await Create("accounts", new { name = "Reserva sem saldo", type = "Savings", financialScope = "PF" });
        var income = await Create("categories", new { name = "Receitas", type = "Income" });
        var expense = await Create("categories", new { name = "Despesas", type = "Expense" });
        return new(session, workspace, pf, pj, reserve, income, expense);
    }

    private async Task<JsonElement> GetAsync(Setup s, string url, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await SendAsync(HttpMethod.Get, url, s.Session);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {body}");
        return string.IsNullOrEmpty(body) ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task PlannedAsync(Setup s, Guid account, string type, string amount, DateOnly due, string description)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"{s.Root}/planned-transactions", s.Session,
            new { accountId = account, categoryId = type == "Income" ? s.Income : s.Expense, type, amount, dueDate = due, description });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static string Text(JsonElement element, params string[] path)
    {
        foreach (var part in path) element = element.GetProperty(part);
        return element.GetString()!;
    }

    private static JsonElement Account(JsonElement overview, Guid id) =>
        overview.GetProperty("accounts").GetProperty("items").EnumerateArray().Single(a => a.GetProperty("accountId").GetGuid() == id);

    // ---- contas ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AccountsShowTheirOwnBalanceThePrimaryAndAnUnavailableBalanceIsNeverZero()
    {
        var s = await ArrangeAsync("ov-accounts");
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"{s.Root}/transactions", s.Session,
            new { accountId = s.Pf, categoryId = s.Income, type = "Income", amount = "250.50", transactionDate = Today, description = "Receita" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var overview = await GetAsync(s, s.Overview("?scope=all"));

        var items = overview.GetProperty("accounts").GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["Conta PF", "Conta PJ", "Reserva sem saldo"], items.Select(i => Text(i, "name")).ToArray()); // principal primeiro, depois por nome
        Assert.Equal(("1250.50", true, true), (Text(Account(overview, s.Pf), "balance"), Account(overview, s.Pf).GetProperty("balanceAvailable").GetBoolean(), Account(overview, s.Pf).GetProperty("isPrimary").GetBoolean()));
        Assert.Equal("500.00", Text(Account(overview, s.Pj), "balance"));
        var reserve = Account(overview, s.Reserve);
        Assert.Equal((false, JsonValueKind.Null, "OpeningBalanceNotConfigured"), (reserve.GetProperty("balanceAvailable").GetBoolean(), reserve.GetProperty("balance").ValueKind, Text(reserve, "unavailableReason")));
        Assert.Equal(Today.ToString("yyyy-MM-dd"), Text(overview, "accounts", "asOf"));
    }

    [Theory]
    [InlineData("primary", "Conta PF")]
    [InlineData("all", "Conta PF,Conta PJ,Reserva sem saldo")]
    [InlineData("account-pj", "Conta PJ")]
    public async Task TheScopeOnlyMarksWhichAccountsAreIncluded(string scope, string expected)
    {
        var s = await ArrangeAsync("ov-scope-" + scope);
        var query = scope == "account-pj" ? $"?scope=account&accountId={s.Pj}" : $"?scope={scope}";

        var overview = await GetAsync(s, s.Overview(query));

        var included = overview.GetProperty("accounts").GetProperty("items").EnumerateArray().Where(i => i.GetProperty("inScope").GetBoolean()).Select(i => Text(i, "name")).Order().ToArray();
        Assert.Equal(expected.Split(',').Order().ToArray(), included);
        Assert.Equal(scope == "account-pj" ? "account" : scope, Text(overview, "scopeKind"));
        // Todas as contas continuam listadas: o escopo não esconde saldo, só marca o que entra nos indicadores.
        Assert.Equal(3, overview.GetProperty("accounts").GetProperty("items").GetArrayLength());
    }

    // ---- compromissos ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CommitmentsSeparateOverdueFromUpcomingWithinTheHorizonAndListOnlyPendingExpenses()
    {
        var s = await ArrangeAsync("ov-commitments");
        await PlannedAsync(s, s.Pf, "Expense", "100.10", Today.AddDays(-3), "Vencida PF");
        await PlannedAsync(s, s.Pf, "Expense", "50.00", Today, "Vence hoje");
        await PlannedAsync(s, s.Pj, "Expense", "200.00", Today.AddDays(10), "Próxima PJ");
        await PlannedAsync(s, s.Pf, "Expense", "999.00", Today.AddDays(45), "Fora do horizonte");
        await PlannedAsync(s, s.Pf, "Income", "700.00", Today.AddDays(2), "Receita futura");

        var all = (await GetAsync(s, s.Overview("?scope=all"))).GetProperty("commitments");

        Assert.Equal(("Complete", 30), (Text(all, "state"), all.GetProperty("horizonDays").GetInt32()));
        Assert.Equal(Today.AddDays(30).ToString("yyyy-MM-dd"), Text(all, "horizonThrough"));
        var overdue = all.GetProperty("overdue");
        Assert.Equal((1, "100.10"), (overdue.GetProperty("count").GetInt32(), Text(overdue, "total")));
        Assert.Equal(("Vencida PF", -3), (Text(overdue.GetProperty("items")[0], "description"), overdue.GetProperty("items")[0].GetProperty("daysFromToday").GetInt32()));
        var upcoming = all.GetProperty("upcoming");
        Assert.Equal((2, "250.00"), (upcoming.GetProperty("count").GetInt32(), Text(upcoming, "total"))); // sem a receita e sem o item fora do horizonte
        Assert.Equal(["Vence hoje", "Próxima PJ"], upcoming.GetProperty("items").EnumerateArray().Select(i => Text(i, "description")).ToArray());

        // Escopo de uma conta: só os compromissos dela.
        var pj = (await GetAsync(s, s.Overview($"?scope=account&accountId={s.Pj}"))).GetProperty("commitments");
        Assert.Equal((0, 1, "200.00"), (pj.GetProperty("overdue").GetProperty("count").GetInt32(), pj.GetProperty("upcoming").GetProperty("count").GetInt32(), Text(pj, "upcoming", "total")));
    }

    [Fact]
    public async Task AListLongerThanTheLimitKeepsTheExactCountAndTotalWhileShowingOnlyTheFirstItems()
    {
        var s = await ArrangeAsync("ov-limit");
        for (var i = 1; i <= 7; i++) await PlannedAsync(s, s.Pf, "Expense", "10.01", Today.AddDays(i), $"Item {i}");

        var upcoming = (await GetAsync(s, s.Overview("?scope=all"))).GetProperty("commitments").GetProperty("upcoming");

        Assert.Equal((7, "70.07", 5), (upcoming.GetProperty("count").GetInt32(), Text(upcoming, "total"), upcoming.GetProperty("items").GetArrayLength()));
        Assert.Equal("Item 1", Text(upcoming.GetProperty("items")[0], "description"));
    }

    [Fact]
    public async Task ARealizedPlannedExpenseIsNoLongerACommitment()
    {
        var s = await ArrangeAsync("ov-realized");
        await PlannedAsync(s, s.Pf, "Expense", "80.00", Today.AddDays(1), "Será paga");
        var planned = (await GetAsync(s, $"{s.Root}/planned-transactions?pageSize=10")).GetProperty("items")[0].GetProperty("id").GetGuid();
        var realize = await SendWithCsrfAsync(HttpMethod.Post, $"{s.Root}/planned-transactions/{planned}/realize", s.Session, new { amount = "80.00", transactionDate = Today });
        Assert.True(realize.IsSuccessStatusCode, await realize.Content.ReadAsStringAsync());

        var commitments = (await GetAsync(s, s.Overview("?scope=all"))).GetProperty("commitments");

        Assert.Equal((0, 0), (commitments.GetProperty("overdue").GetProperty("count").GetInt32(), commitments.GetProperty("upcoming").GetProperty("count").GetInt32()));
    }

    // ---- metas -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task GoalsShowAllocatedTargetAndProgressForActiveGoalsOnly()
    {
        var s = await ArrangeAsync("ov-goals");
        async Task<Guid> Goal(string name) => (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{s.Root}/financial-goals", s.Session,
            new { name, goalType = "EmergencyFund", targetAmount = "1000.00", priority = "High" }))).Id;
        var reserve = await Goal("Reserva");
        var paused = await Goal("Pausada");
        Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, $"{s.Root}/financial-goals/{reserve}/contributions", s.Session,
            new { operationId = Guid.NewGuid(), financialAccountId = s.Pf, amount = "250.00", effectiveDate = Today })).StatusCode);
        var pause = await SendWithCsrfAsync(HttpMethod.Post, $"{s.Root}/financial-goals/{paused}/pause", s.Session); // rota corrigida na R01 (o parâmetro não casava com a rota)
        Assert.True(pause.IsSuccessStatusCode, await pause.Content.ReadAsStringAsync());

        var goals = (await GetAsync(s, s.Overview())).GetProperty("goals");

        Assert.Equal(("Complete", 1), (Text(goals, "state"), goals.GetProperty("activeCount").GetInt32()));
        var item = Assert.Single(goals.GetProperty("items").EnumerateArray());
        Assert.Equal(("Reserva", "1000.00", "250.00", "25.0", false), (Text(item, "name"), Text(item, "target"), Text(item, "allocated"), Text(item, "progressPercent"), item.GetProperty("funded").GetBoolean()));
    }

    [Fact]
    public async Task WithoutGoalsOrCommitmentsTheBlocksAreEmptyNotFailed()
    {
        var s = await ArrangeAsync("ov-empty");

        var overview = await GetAsync(s, s.Overview());

        Assert.Equal(("Complete", 0), (Text(overview, "goals", "state"), overview.GetProperty("goals").GetProperty("items").GetArrayLength()));
        Assert.Equal(("Complete", "0.00", "0.00"), (Text(overview, "commitments", "state"), Text(overview, "commitments", "overdue", "total"), Text(overview, "commitments", "upcoming", "total")));
    }


    // ---- orçamento do mês ------------------------------------------------------------------------------------------------------

    private async Task<Guid> BudgetWithTargetAsync(Setup s, string competence, string name, decimal planned, Guid category)
    {
        var budget = (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{s.Root}/budgets", s.Session, new { competence }))).Id;
        var added = await SendWithCsrfAsync(HttpMethod.Post, $"{s.Root}/budgets/{budget}/targets", s.Session, new { name, plannedAmount = planned, groupingType = "Category", categoryId = category });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        return budget;
    }

    [Fact]
    public async Task TheBudgetBlockListsEachCategoryLimitWithItsConsumptionAndStatusMostSevereFirst()
    {
        var s = await ArrangeAsync("ov-budget");
        var food = (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{s.Root}/categories", s.Session, new { name = "Alimentação", type = "Expense" }))).Id;
        var month = $"{Today.Year:0000}-{Today.Month:00}";
        var budget = await BudgetWithTargetAsync(s, month, "Despesas", 100.00m, s.Expense);
        Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, $"{s.Root}/budgets/{budget}/targets", s.Session, new { name = "Alimentação", plannedAmount = 500.00m, groupingType = "Category", categoryId = food })).StatusCode);
        foreach (var (amount, category) in new[] { ("130.00", s.Expense), ("50.00", food) })
            Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, $"{s.Root}/transactions", s.Session,
                new { accountId = s.Pf, categoryId = category, type = "Expense", amount, transactionDate = Today, description = "Consumo" })).StatusCode);

        var block = (await GetAsync(s, s.Overview("?scope=all"))).GetProperty("budget");

        var categories = block.GetProperty("categories");
        Assert.Equal(("Complete", true, 1, 2), (Text(block, "state"), block.GetProperty("hasBudget").GetBoolean(), categories.GetProperty("exceededCount").GetInt32(), categories.GetProperty("count").GetInt32()));
        // Resumo só das metas de categoria (não se sobrepõem): 600 de limite e 180 consumidos. Sem metas de fixas e cartões, não há esses grupos.
        Assert.Equal(("600.00", "180.00"), (Text(categories, "planned"), Text(categories, "consumed")));
        Assert.Equal(JsonValueKind.Null, block.GetProperty("fixed").ValueKind);
        Assert.Equal(JsonValueKind.Null, block.GetProperty("cards").ValueKind);
        var targets = categories.GetProperty("targets").EnumerateArray().ToList();
        Assert.Equal(["Despesas", "Alimentação"], targets.Select(t => Text(t, "name")).ToArray()); // o excedido vem primeiro
        Assert.Equal(("100.00", "130.00", "30.00", "Exceeded"), (Text(targets[0], "planned"), Text(targets[0], "consumed"), Text(targets[0], "exceeded"), Text(targets[0], "status")));
        Assert.Equal(("500.00", "50.00", "450.00", "WithinBudget"), (Text(targets[1], "planned"), Text(targets[1], "consumed"), Text(targets[1], "available"), Text(targets[1], "status")));
    }

    [Fact]
    public async Task WithoutABudgetForTheMonthTheBlockSaysSoInsteadOfShowingZero()
    {
        var s = await ArrangeAsync("ov-no-budget");

        var block = (await GetAsync(s, s.Overview("?competence=2031-05"))).GetProperty("budget");

        Assert.Equal(("Complete", false, 0), (Text(block, "state"), block.GetProperty("hasBudget").GetBoolean(), block.GetProperty("categories").GetProperty("targets").GetArrayLength()));
        Assert.Equal(JsonValueKind.Null, block.GetProperty("categories").GetProperty("planned").ValueKind);
        Assert.Equal(JsonValueKind.Null, block.GetProperty("fixed").ValueKind);
        Assert.Equal(JsonValueKind.Null, block.GetProperty("cards").ValueKind);
    }

    // ---- últimos lançamentos e classificação ------------------------------------------------------------------------------------

    [Fact]
    public async Task RecentTransactionsAreTheMonthsLatestEffectiveOnesOfTheSelectedScopeAndAFutureMonthIsEmpty()
    {
        var s = await ArrangeAsync("ov-recent");
        async Task Create(Guid account, string type, string amount, DateOnly date, string description) =>
            Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, $"{s.Root}/transactions", s.Session,
                new { accountId = account, categoryId = type == "Income" ? s.Income : s.Expense, type, amount, transactionDate = date, description })).StatusCode);
        var first = new DateOnly(Today.Year, Today.Month, 1);
        await Create(s.Pf, "Expense", "10.00", first, "Mais antigo");
        await Create(s.Pf, "Income", "20.00", Today, "Mais novo");
        await Create(s.Pj, "Expense", "30.00", Today, "Da conta PJ");

        var all = (await GetAsync(s, s.Overview("?scope=all"))).GetProperty("recent");
        var primary = (await GetAsync(s, s.Overview("?scope=primary"))).GetProperty("recent");
        var future = (await GetAsync(s, s.Overview($"?scope=all&competence={Today.AddMonths(2).Year:0000}-{Today.AddMonths(2).Month:00}"))).GetProperty("recent");

        Assert.Equal((3, first.ToString("yyyy-MM-dd"), Today.ToString("yyyy-MM-dd")), (all.GetProperty("totalCount").GetInt32(), Text(all, "from"), Text(all, "through")));
        Assert.Equal(["Da conta PJ", "Mais novo", "Mais antigo"], all.GetProperty("items").EnumerateArray().Select(i => Text(i, "description")).ToArray()); // data decrescente e, no mesmo dia, o mais recente primeiro
        Assert.Equal(2, primary.GetProperty("totalCount").GetInt32()); // a conta principal não vê o lançamento da PJ
        Assert.DoesNotContain(primary.GetProperty("items").EnumerateArray(), i => Text(i, "description") == "Da conta PJ");
        Assert.Equal(("Complete", 0), (Text(future, "state"), future.GetProperty("items").GetArrayLength()));
    }

    [Fact]
    public async Task ClassificationQueueIsReportedApartFromSyncAndEmptyByDefault()
    {
        var s = await ArrangeAsync("ov-classification");

        var classification = (await GetAsync(s, s.Overview())).GetProperty("classification");

        Assert.Equal(("Complete", 0, 0), (Text(classification, "state"), classification.GetProperty("needsReview").GetInt32(), classification.GetProperty("notEvaluated").GetInt32()));
    }

    // ---- acesso e validação ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AccessAndValidationFollowTheDashboardContract()
    {
        var s = await ArrangeAsync("ov-access");
        var stranger = await SignInAsync("ov-stranger");
        await CreateWorkspaceAsync(stranger);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync(s.Overview())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, s.Overview(), stranger)).StatusCode);
        await GetAsync(s, s.Overview("?scope=todas"), HttpStatusCode.BadRequest);
        await GetAsync(s, s.Overview("?scope=account"), HttpStatusCode.BadRequest);
        await GetAsync(s, s.Overview("?competence=2026-13"), HttpStatusCode.BadRequest);
        await GetAsync(s, s.Overview($"?scope=account&accountId={Guid.NewGuid()}"), HttpStatusCode.NotFound);
        var response = await SendAsync(HttpMethod.Get, s.Overview(), s.Session);
        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    // ---- últimos lançamentos: o pagamento de fatura não é uma despesa a mais --------------------------------------------------

    [Fact]
    public async Task TheTransactionListCanOmitInvoicePaymentsSoTheCardPurchasesAreNotCountedTwice()
    {
        var s = await ArrangeAsync("ov-invoice-payments");
        Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, $"{s.Root}/transactions", s.Session,
            new { accountId = s.Pf, categoryId = s.Expense, type = "Expense", amount = "12.34", transactionDate = Today, description = "Padaria" })).StatusCode);
        var card = (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{s.Root}/credit-cards", s.Session,
            new { name = "Cartão", issuer = "Banco", creditLimit = "5000.00", closingDay = 5, dueDay = 15, defaultPaymentAccountId = s.Pf }))).Id;
        Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, $"{s.Root}/credit-cards/{card}/purchases", s.Session,
            new { description = "Compra", purchaseDate = Today, totalAmount = "300.00", installmentCount = 1, categoryId = s.Expense })).StatusCode);
        Guid invoice;
        await using (var db = Db()) invoice = (await db.FinancialCreditCardInvoices.AsNoTracking().Where(i => i.CreditCardId == card).OrderBy(i => i.DueDate).FirstAsync()).Id;
        await using (var official = Db()) await OfficialInvoiceFixture.CloseAsync(official, invoice);
        var payment = await SendWithCsrfAsync(HttpMethod.Post, $"{s.Root}/credit-cards/{card}/invoices/{invoice}/payments", s.Session,
            new { operationId = Guid.NewGuid(), financialAccountId = s.Pf, paymentDate = Today, amount = "300.00" });
        Assert.Equal(HttpStatusCode.Created, payment.StatusCode);

        var everything = await GetAsync(s, $"{s.Root}/transactions?pageSize=20");
        var withoutPayments = await GetAsync(s, $"{s.Root}/transactions?pageSize=20&excludeInvoicePayments=true");

        Assert.Equal(["CreditCardInvoicePayment", "Manual"], everything.GetProperty("items").EnumerateArray().Select(i => Text(i, "origin")).Order().ToArray());
        var remaining = Assert.Single(withoutPayments.GetProperty("items").EnumerateArray());
        Assert.Equal(("Padaria", "12.34"), (Text(remaining, "description"), Text(remaining, "amount")));
        Assert.Equal(1, withoutPayments.GetProperty("totalCount").GetInt32());
    }
}
