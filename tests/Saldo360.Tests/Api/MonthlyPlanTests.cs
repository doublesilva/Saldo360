using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.Common;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>
/// T10.2: planejamento mensal, com PostgreSQL real e sem Pluggy nem OpenAI. O plano é META do orçamento: não cria lançamento, previsão, pagamento nem
/// transferência, não altera a Agenda e é a fonte do "planejado" do Dashboard; a realização e a projeção continuam vindo do núcleo financeiro.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Planning")]
[Trait("Dependency", "PostgreSQL")]
public sealed class MonthlyPlanTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private static DateOnly Today => BusinessCalendar.Today(DateTimeOffset.UtcNow);
    private static DateOnly ThisMonth => new(Today.Year, Today.Month, 1);
    private static string Month(DateOnly date) => date.ToString("yyyy-MM");
    // Mês fixo, distante, para os testes de calendário: não dependem do dia em que a suíte roda. Dezembro de 2031: 31 dias, começa numa segunda, 23 dias de segunda a sexta.
    private const string Fixed = "2031-12";

    private sealed record Setup(string Session, WorkspaceDto Workspace, Guid Pf, Guid Pj, Guid Income, Guid Expense, Guid Food, Guid Housing)
    {
        public string Root => $"/api/financial-workspaces/{Workspace.Id}";
        public string Plan => $"{Root}/monthly-plan";
    }

    private sealed record IdDto(Guid Id);

    private async Task<Setup> ArrangeAsync(string user, bool withOpening = true)
    {
        var session = await SignInAsync(user);
        var workspace = await CreateWorkspaceAsync(session);
        var root = $"/api/financial-workspaces/{workspace.Id}";
        async Task<Guid> Create(string path, object body) => (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{root}/{path}", session, body))).Id;
        var opening = ThisMonth.AddMonths(-1);
        var pf = await Create("accounts", new { name = "Itaú PF", type = "Checking", financialScope = "PF", isPrimary = true, openingBalance = withOpening ? "1000.00" : null, openingBalanceDate = withOpening ? opening : (DateOnly?)null });
        var pj = await Create("accounts", new { name = "Itaú PJ", type = "Checking", financialScope = "PJ", openingBalance = withOpening ? "500.00" : null, openingBalanceDate = withOpening ? opening : (DateOnly?)null });
        var income = await Create("categories", new { name = "Receitas", type = "Income" });
        var expense = await Create("categories", new { name = "Despesas", type = "Expense" });
        var food = await Create("categories", new { name = "Alimentação", type = "Expense" });
        var housing = await Create("categories", new { name = "Moradia", type = "Expense" });
        return new(session, workspace, pf, pj, income, expense, food, housing);
    }

    // ---- Auxiliares HTTP -----------------------------------------------------------------------------------------------------------------

    private async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {body}");
        return string.IsNullOrEmpty(body) ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private Task<JsonElement> GetAsync(Setup s, string path, HttpStatusCode expected = HttpStatusCode.OK) => GetAsync(s.Session, s.Plan + path, expected);
    private async Task<JsonElement> GetAsync(string session, string url, HttpStatusCode expected = HttpStatusCode.OK) => await ReadJsonAsync(await SendAsync(HttpMethod.Get, url, session), expected);

    private Task<JsonElement> PlanAsync(Setup s, string? month = null, string? scope = null, Guid? account = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var query = new List<string>();
        if (month is not null) query.Add($"competence={month}");
        if (scope is not null) query.Add($"scope={scope}");
        if (account is { } id) query.Add($"accountId={id}");
        return GetAsync(s, query.Count == 0 ? "" : "?" + string.Join('&', query), expected);
    }

    private Task<JsonElement> DashboardAsync(Setup s, string? month = null, string? scope = null, Guid? account = null)
    {
        var query = new List<string>();
        if (month is not null) query.Add($"competence={month}");
        if (scope is not null) query.Add($"scope={scope}");
        if (account is { } id) query.Add($"accountId={id}");
        return GetAsync(s.Session, $"{s.Root}/financial-dashboard/plan-actual" + (query.Count == 0 ? "" : "?" + string.Join('&', query)));
    }

    private Task<JsonElement> SixMonthsAsync(Setup s, string? from = null, string? scope = null, Guid? account = null)
    {
        var query = new List<string>();
        if (from is not null) query.Add($"from={from}");
        if (scope is not null) query.Add($"scope={scope}");
        if (account is { } id) query.Add($"accountId={id}");
        return GetAsync(s, "/six-months" + (query.Count == 0 ? "" : "?" + string.Join('&', query)));
    }

    private async Task<JsonElement> LineAsync(Setup s, object body, HttpStatusCode expected = HttpStatusCode.Created) =>
        await ReadJsonAsync(await SendWithCsrfAsync(HttpMethod.Post, s.Plan + "/lines", s.Session, body), expected);

    private async Task<JsonElement> ReviseAsync(Setup s, string id, object body, HttpStatusCode expected = HttpStatusCode.OK) =>
        await ReadJsonAsync(await SendWithCsrfAsync(HttpMethod.Put, $"{s.Plan}/lines/{id}", s.Session, body), expected);

    private async Task<JsonElement> LimitAsync(Setup s, string month, Guid category, string amount, string? note = null, HttpStatusCode expected = HttpStatusCode.OK) =>
        await ReadJsonAsync(await SendWithCsrfAsync(HttpMethod.Put, s.Plan + "/category-limits", s.Session, new { competence = month, categoryId = category, amount, note }), expected);

    private static object Income(string month, string amount, Guid? account, string description = "Receita", string nature = "Recurring", DateOnly? expected = null) =>
        new { competence = month, kind = "Income", nature, description, accountId = account, amount, expectedDate = expected };

    private static object Expense(string month, string amount, Guid? account, Guid? category = null, string description = "Despesa", string nature = "Fixed") =>
        new { competence = month, kind = "Expense", nature, description, accountId = account, categoryId = category, amount };

    private static string Text(JsonElement element, params string[] path) { foreach (var part in path) element = element.GetProperty(part); return element.GetString()!; }
    private static bool IsNull(JsonElement element, params string[] path) { foreach (var part in path) element = element.GetProperty(part); return element.ValueKind == JsonValueKind.Null; }
    private static JsonElement Item(JsonElement array, Func<JsonElement, bool> match) => array.EnumerateArray().Single(match);

    private async Task<HttpResponseMessage> PostAsync(Setup s, string path, object body) => await SendWithCsrfAsync(HttpMethod.Post, $"{s.Root}/{path}", s.Session, body);

    private async Task<Guid> TransactionAsync(Setup s, Guid account, string type, string amount, DateOnly date, Guid? category = null) =>
        (await ReadAsync<IdDto>(await PostAsync(s, "transactions", new { accountId = account, categoryId = category ?? (type == "Income" ? s.Income : s.Expense), type, amount, transactionDate = date, description = "Sintético" }))).Id;

    private async Task<Guid> PlannedAsync(Setup s, Guid account, string type, string amount, DateOnly due) =>
        (await ReadAsync<IdDto>(await PostAsync(s, "planned-transactions", new { accountId = account, categoryId = type == "Income" ? s.Income : s.Expense, type, amount, dueDate = due, description = "Previsão" }))).Id;

    private async Task<(int Transactions, int Planned, int Transfers, int Targets)> FinancialCountsAsync()
    {
        await using var db = Db();
        return (await db.FinancialTransactions.CountAsync(), await db.FinancialPlannedTransactions.CountAsync(), await db.FinancialTransfers.CountAsync(), await db.FinancialBudgetTargets.CountAsync(t => t.GroupingType == Saldo360.Domain.Budgets.BudgetTargetGroupingType.AllCards));
    }

    // ---- 1, 19: mês sem planejamento, e planejamento mensal configurável ---------------------------------------------------------------

    [Fact]
    public async Task AMonthWithoutPlanningIsNotDefinedAndNeverAFictionalZero()
    {
        var s = await ArrangeAsync("mp-empty");

        var plan = await PlanAsync(s);

        Assert.Equal(ThisMonth.ToString("yyyy-MM"), Text(plan, "competence"));
        Assert.False(plan.GetProperty("hasPlan").GetBoolean());
        Assert.True(IsNull(plan, "planned", "income") && IsNull(plan, "planned", "expense") && IsNull(plan, "planned", "result"));
        Assert.Equal(0, plan.GetProperty("lines").GetArrayLength());
        Assert.Equal(0, plan.GetProperty("categoryLimits").GetArrayLength());
        Assert.Contains(plan.GetProperty("pendencies").EnumerateArray(), p => p.GetProperty("code").GetString() == "NoPlan");
        // O Dashboard mostra "não definido" para o planejado e continua exibindo o realizado.
        Assert.Equal("NotDefined", Text(plan, "execution", "income", "plannedState"));
        Assert.Equal("NotDefined", Text(plan, "execution", "consumption", "plannedState"));
        Assert.Equal(("primary", s.Pf.ToString()), (Text(plan, "scope", "kind"), Text(plan, "scope", "accountId")));
    }

    [Fact]
    public async Task APlannedIncomeFeedsTheDashboardAndNeverCreatesARealizedMovementOrACommitment()
    {
        var s = await ArrangeAsync("mp-income");
        var before = await FinancialCountsAsync();

        var line = await LineAsync(s, Income(Month(ThisMonth), "7500.50", s.Pf, "Salário", expected: ThisMonth.AddDays(4)));
        var dashboard = await DashboardAsync(s);
        var plan = await PlanAsync(s);

        Assert.Equal(("Income", "Recurring", "7500.50", "7500.50", "0.00", false), (Text(line, "kind"), Text(line, "nature"), Text(line, "originalAmount"), Text(line, "amount"), Text(line, "difference"), line.GetProperty("revised").GetBoolean()));
        // "Não definido" deixa de aparecer: o planejado é o do módulo, com a origem identificada, e o realizado continua vindo do núcleo (zero movimentos).
        Assert.Equal(("Defined", "7500.50", "MonthlyPlan", "0.00"), (Text(dashboard, "income", "plannedState"), Text(dashboard, "income", "planned"), Text(dashboard, "income", "plannedSource"), Text(dashboard, "income", "realized")));
        Assert.Equal("7500.50", Text(plan, "planned", "income"));
        Assert.Equal(before, await FinancialCountsAsync());
        await using var db = Db();
        Assert.Equal(0, await db.FinancialAccountBalanceAnchors.CountAsync());
    }

    [Fact]
    public async Task PlannedExpensesUseTheSameConsumptionBaseAsTheDashboardAndTheCashFlowKeepsItsCommitments()
    {
        var s = await ArrangeAsync("mp-expense");
        await LineAsync(s, Expense(Month(ThisMonth), "1800.00", s.Pf, s.Housing, "Aluguel"));
        await LineAsync(s, Expense(Month(ThisMonth), "450.25", s.Pf, null, "Plano de saúde", "Recurring"));
        await TransactionAsync(s, s.Pf, "Expense", "300.00", Today);
        await PlannedAsync(s, s.Pf, "Expense", "90.00", ThisMonth.AddDays(Math.Min(27, 20)));

        var dashboard = await DashboardAsync(s);
        var consumption = dashboard.GetProperty("expenses").GetProperty("consumption");
        var cash = dashboard.GetProperty("expenses").GetProperty("cashFlow");

        Assert.Equal(("Defined", "2250.25", "MonthlyPlan", "300.00"), (Text(consumption, "plannedState"), Text(consumption, "planned"), Text(consumption, "plannedSource"), Text(consumption, "realized")));
        // O fluxo de caixa mantém a base das previsões cadastradas (compromissos), e o plano não vira previsão.
        Assert.Equal(("90.00", "PlannedTransactions"), (Text(cash, "planned"), Text(cash, "plannedSource")));
    }

    [Fact]
    public async Task ReservesAndProvisionsAreOutsideTheConsumptionBaseAndNeverASavedOutflow()
    {
        var s = await ArrangeAsync("mp-reserve");
        await LineAsync(s, Expense(Month(ThisMonth), "1000.00", s.Pf));
        var reserve = await LineAsync(s, Expense(Month(ThisMonth), "400.00", s.Pf, null, "Reserva de emergência", "Reserve"));

        var plan = await PlanAsync(s);
        var dashboard = await DashboardAsync(s);

        Assert.Equal(("1000.00", "400.00", 1), (Text(plan, "planned", "expense"), Text(plan, "planned", "reserve"), plan.GetProperty("planned").GetProperty("reserveLines").GetInt32()));
        Assert.Equal("1000.00", Text(dashboard, "expenses", "consumption", "planned"));
        Assert.Equal("Reserve", Text(reserve, "nature"));
        // Reserva é só despesa.
        await LineAsync(s, new { competence = Month(ThisMonth), kind = "Income", nature = "Reserve", description = "x", accountId = s.Pf, amount = "10.00" }, HttpStatusCode.BadRequest);
    }

    // ---- 4–6: limites por categoria, revisões, histórico -------------------------------------------------------------------------------

    [Fact]
    public async Task ACategoryLimitIsACeilingNeverAnObligationAndItsExecutionUsesTheCategoriesAssignedBySaldo360()
    {
        var s = await ArrangeAsync("mp-limit");
        var before = await FinancialCountsAsync();
        var month = Month(ThisMonth);

        var limit = await LimitAsync(s, month, s.Food, "2500.00", "Teto de alimentação");
        await LimitAsync(s, month, s.Housing, "2500.00");
        var transaction = await TransactionAsync(s, s.Pf, "Expense", "600.00", Today, s.Food);
        var plan = await PlanAsync(s);

        Assert.Equal(("Alimentação", "2500.00", "2500.00", false), (Text(limit, "categoryName"), Text(limit, "amount"), Text(limit, "originalAmount"), limit.GetProperty("revised").GetBoolean()));
        var food = Item(plan.GetProperty("categoryLimits"), l => l.GetProperty("categoryName").GetString() == "Alimentação");
        Assert.Equal(("600.00", "1900.00", "WithinBudget"), (Text(food, "execution", "realized"), Text(food, "execution", "available"), Text(food, "execution", "status")));
        // Os dois limites de R$ 2.500 formam o total planejado de R$ 5.000; criar um teto não cria despesa, previsão ou lançamento.
        var all = await PlanAsync(s, scope: "all");
        Assert.Equal("5000.00", Text(all, "planned", "expense"));
        // Na conta principal o limite por categoria é consolidado, então não é atribuído a ela (compras de cartão não têm atribuição inequívoca a uma conta).
        Assert.True(IsNull(plan, "planned", "expense"));
        Assert.True(plan.GetProperty("planned").GetProperty("limitsNotAttributable").GetBoolean());
        Assert.Contains(plan.GetProperty("pendencies").EnumerateArray(), p => p.GetProperty("code").GetString() == "LimitsNotAttributable");
        Assert.Equal(before with { Transactions = before.Transactions + 1 }, await FinancialCountsAsync());
        // Reclassificar o lançamento atualiza a execução do orçamento: a despesa sai de Alimentação e vai para Moradia.
        var reclassified = await SendWithCsrfAsync(HttpMethod.Put, $"{s.Root}/transactions/{transaction}", s.Session,
            new { accountId = s.Pf, categoryId = s.Housing, type = "Expense", amount = "600.00", transactionDate = Today, description = "Sintético" });
        Assert.Equal(HttpStatusCode.OK, reclassified.StatusCode);
        var after = await PlanAsync(s);
        Assert.Equal("0.00", Text(Item(after.GetProperty("categoryLimits"), l => l.GetProperty("categoryName").GetString() == "Alimentação"), "execution", "realized"));
        Assert.Equal("600.00", Text(Item(after.GetProperty("categoryLimits"), l => l.GetProperty("categoryName").GetString() == "Moradia"), "execution", "realized"));
        // O planejamento original não foi reescrito pelo gasto.
        Assert.Equal("2500.00", Text(Item(after.GetProperty("categoryLimits"), l => l.GetProperty("categoryName").GetString() == "Alimentação"), "amount"));
    }

    [Fact]
    public async Task ItemizedLinesInsideALimitedCategoryDetailTheLimitWithoutAddingToTheTotal()
    {
        var s = await ArrangeAsync("mp-itemized");
        var month = Month(ThisMonth);
        await LimitAsync(s, month, s.Housing, "2500.00");
        await LimitAsync(s, month, s.Food, "2500.00");
        await LineAsync(s, Expense(month, "2000.00", s.Pf, s.Housing, "Aluguel"));
        await LineAsync(s, Expense(month, "900.00", s.Pf, s.Housing, "Condomínio e luz"));
        await LineAsync(s, Expense(month, "300.00", s.Pj, null, "Contabilidade"));

        var plan = await PlanAsync(s, scope: "all");
        var dashboard = await DashboardAsync(s, scope: "all");

        // 2.500 (Moradia) + 2.500 (Alimentação) + 300 (linha sem limite) = 5.300; as linhas de Moradia não somam de novo.
        Assert.Equal("5300.00", Text(plan, "planned", "expense"));
        Assert.Equal(("5300.00", "MonthlyPlan"), (Text(dashboard, "expenses", "consumption", "planned"), Text(dashboard, "expenses", "consumption", "plannedSource")));
        var housing = Item(plan.GetProperty("categoryLimits"), l => l.GetProperty("categoryName").GetString() == "Moradia");
        Assert.Equal(("2900.00", true), (Text(housing, "itemizedAmount"), housing.GetProperty("itemizedAboveLimit").GetBoolean()));
        Assert.Contains(plan.GetProperty("pendencies").EnumerateArray(), p => p.GetProperty("code").GetString() == "ItemizedAboveLimit");
    }

    [Fact]
    public async Task RevisingALineKeepsTheOriginalAmountUpdatesTheDashboardAndRecordsTheHistory()
    {
        var s = await ArrangeAsync("mp-revise");
        var month = Month(ThisMonth);
        var created = await LineAsync(s, Income(month, "5000.00", s.Pf, "Consultoria"));
        var id = Text(created, "id");

        var revised = await ReviseAsync(s, id, new { kind = "Income", nature = "Recurring", description = "Consultoria", accountId = s.Pf, amount = "5600.00", note = "Reajuste do contrato" });
        var again = await ReviseAsync(s, id, new { kind = "Income", nature = "Recurring", description = "Consultoria", accountId = s.Pf, amount = "5600.00" });
        var dashboard = await DashboardAsync(s);
        var history = await GetAsync(s, $"/history?competence={month}&subjectId={id}");

        Assert.Equal(("5000.00", "5600.00", "600.00", true), (Text(revised, "originalAmount"), Text(revised, "amount"), Text(revised, "difference"), revised.GetProperty("revised").GetBoolean()));
        Assert.Equal("5600.00", Text(again, "amount"));
        Assert.Equal("5600.00", Text(dashboard, "income", "planned"));
        // Duas entradas (criação e revisão): repetir a mesma revisão não grava histórico novo.
        var entries = history.EnumerateArray().ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal(("Revised", "5000.00", "5600.00", "Reajuste do contrato"), (Text(entries[0], "action"), Text(entries[0], "previousAmount"), Text(entries[0], "newAmount"), Text(entries[0], "note")));
        Assert.Equal(("Created", "5000.00"), (Text(entries[1], "action"), Text(entries[1], "newAmount")));
        Assert.True(IsNull(entries[1], "previousAmount"));
        Assert.Equal("Consultoria", Text(entries[0], "label"));
    }

    [Fact]
    public async Task ACategoryLimitKeepsItsOriginalAmountAcrossRevisionsAndTheHistoryCoversRemovalAndRestoration()
    {
        var s = await ArrangeAsync("mp-limit-history");
        var month = Month(ThisMonth);
        await LimitAsync(s, month, s.Food, "2000.00");
        var revised = await LimitAsync(s, month, s.Food, "2600.00", "Inflação de alimentos");
        var delete = await SendWithCsrfAsync(HttpMethod.Delete, $"{s.Plan}/category-limits/{s.Food}?competence={month}&note=Sem%20teto", s.Session);
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        var withoutLimit = await PlanAsync(s);
        var restored = await LimitAsync(s, month, s.Food, "2200.00");
        var history = await GetAsync(s, $"/history?competence={month}");

        Assert.Equal(("2000.00", "2600.00", true), (Text(revised, "originalAmount"), Text(revised, "amount"), revised.GetProperty("revised").GetBoolean()));
        Assert.Equal(0, withoutLimit.GetProperty("categoryLimits").GetArrayLength());
        Assert.True(IsNull(withoutLimit, "planned", "expense"));
        Assert.Equal(("2000.00", "2200.00"), (Text(restored, "originalAmount"), Text(restored, "amount")));
        Assert.Equal(["Restored", "Removed", "Revised", "Created"], history.EnumerateArray().Select(e => e.GetProperty("action").GetString()!).ToArray());
        Assert.All(history.EnumerateArray(), e => Assert.Equal("CategoryLimit", e.GetProperty("subject").GetString()));
        Assert.Equal(("2600.00", "Sem teto"), (Text(history.EnumerateArray().ElementAt(1), "previousAmount"), Text(history.EnumerateArray().ElementAt(1), "note")));
    }

    [Fact]
    public async Task LimitsEditedThroughTheExistingBudgetEndpointsAlsoRecordTheHistory()
    {
        var s = await ArrangeAsync("mp-legacy-path");
        var month = Month(ThisMonth);
        var budget = (await ReadAsync<IdDto>(await PostAsync(s, "budgets", new { competence = month, description = "Orçamento" }))).Id;
        var added = await PostAsync(s, $"budgets/{budget}/targets", new { name = "Alimentação", plannedAmount = 1800.00m, groupingType = "Category", categoryId = s.Food });
        Assert.True(added.IsSuccessStatusCode, await added.Content.ReadAsStringAsync());
        var target = Guid.Parse(JsonDocument.Parse(await added.Content.ReadAsStringAsync()).RootElement.GetProperty("targets")[0].GetProperty("id").GetString()!);
        Assert.Equal(HttpStatusCode.OK, (await SendWithCsrfAsync(HttpMethod.Put, $"{s.Root}/budgets/{budget}/targets/{target}", s.Session, new { name = "Alimentação", plannedAmount = 2100.00m })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendWithCsrfAsync(HttpMethod.Delete, $"{s.Root}/budgets/{budget}/targets/{target}", s.Session)).StatusCode);

        var history = await GetAsync(s, $"/history?competence={month}&subjectId={target}");

        Assert.Equal(["Removed", "Revised", "Created"], history.EnumerateArray().Select(e => e.GetProperty("action").GetString()!).ToArray());
        Assert.Equal(("1800.00", "2100.00"), (Text(history[1], "previousAmount"), Text(history[1], "newAmount")));
    }

    [Fact]
    public async Task ALimitCreatedBeforeTheHistoryHasNoKnownOriginalUntilItIsRevisedAndThenTheFirstRevisionRecoversIt()
    {
        var s = await ArrangeAsync("mp-pre-history");
        var month = Month(ThisMonth);
        await using (var db = Db())
        {
            var budget = Saldo360.Domain.Budgets.FinancialBudget.Create(s.Workspace.Id, ThisMonth, null, s.Workspace.OwnerUserId, DateTimeOffset.UtcNow);
            db.FinancialBudgets.Add(budget);
            db.FinancialBudgetTargets.Add(Saldo360.Domain.Budgets.FinancialBudgetTarget.Create(s.Workspace.Id, budget.Id, "Alimentação", 2000m, s.Food, s.Workspace.OwnerUserId, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var before = await PlanAsync(s, scope: "all");
        await LimitAsync(s, month, s.Food, "2300.00");
        var after = await PlanAsync(s, scope: "all");

        var limitBefore = before.GetProperty("categoryLimits")[0];
        Assert.True(IsNull(limitBefore, "originalAmount"));
        Assert.False(limitBefore.GetProperty("revised").GetBoolean());
        var limitAfter = after.GetProperty("categoryLimits")[0];
        Assert.Equal(("2000.00", "2300.00", true), (Text(limitAfter, "originalAmount"), Text(limitAfter, "amount"), limitAfter.GetProperty("revised").GetBoolean()));
    }

    [Fact]
    public async Task RemovingALineKeepsTheRecordAndTheHistoryAndAnArchivedLineCannotBeRevised()
    {
        var s = await ArrangeAsync("mp-remove");
        var month = Month(ThisMonth);
        var line = await LineAsync(s, Expense(month, "100.00", s.Pf));
        var id = Text(line, "id");

        var removed = await SendWithCsrfAsync(HttpMethod.Delete, $"{s.Plan}/lines/{id}?note=Cancelado", s.Session);
        var again = await SendWithCsrfAsync(HttpMethod.Delete, $"{s.Plan}/lines/{id}", s.Session);
        var plan = await PlanAsync(s);
        await ReviseAsync(s, id, Expense(month, "150.00", s.Pf), HttpStatusCode.Conflict);
        var history = await GetAsync(s, $"/history?competence={month}&subjectId={id}");

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (removed.StatusCode, again.StatusCode));
        Assert.Equal(0, plan.GetProperty("lines").GetArrayLength());
        Assert.True(IsNull(plan, "planned", "expense"));
        Assert.Equal(["Removed", "Created"], history.EnumerateArray().Select(e => e.GetProperty("action").GetString()!).ToArray());
        await using var db = Db();
        Assert.Equal(1, await db.FinancialPlanLines.CountAsync());
        Assert.True((await db.FinancialPlanLines.SingleAsync()).IsArchived);
    }

    // ---- 7: cópia do mês anterior -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task CopyingTheMonthRequiresConfirmationCopiesPlanAndLimitsNeverRealizedMovementsAndKeepsTheHistory()
    {
        var s = await ArrangeAsync("mp-copy");
        var from = Month(ThisMonth); var to = Month(ThisMonth.AddMonths(1));
        await LineAsync(s, Income(from, "5000.00", s.Pj, "Receita PJ", expected: ThisMonth.AddDays(27)));
        await LineAsync(s, Expense(from, "1500.00", s.Pf, s.Housing, "Aluguel"));
        await LimitAsync(s, from, s.Food, "2500.00");
        await TransactionAsync(s, s.Pf, "Expense", "77.00", Today);
        var before = await FinancialCountsAsync();

        var withoutConfirmation = await ReadJsonAsync(await SendWithCsrfAsync(HttpMethod.Post, s.Plan + "/copy", s.Session, new { fromCompetence = from, toCompetence = to, confirm = false }), HttpStatusCode.BadRequest);
        var copy = await ReadJsonAsync(await SendWithCsrfAsync(HttpMethod.Post, s.Plan + "/copy", s.Session, new { fromCompetence = from, toCompetence = to, confirm = true }));
        var plan = await PlanAsync(s, to, "all");

        Assert.True(withoutConfirmation.GetProperty("errors").TryGetProperty("confirm", out _));
        Assert.Equal((2, 0, 1, 0), (copy.GetProperty("linesCopied").GetInt32(), copy.GetProperty("linesSkipped").GetInt32(), copy.GetProperty("limitsCopied").GetInt32(), copy.GetProperty("limitsSkipped").GetInt32()));
        Assert.Equal(("5000.00", "4000.00"), (Text(plan, "planned", "income"), Text(plan, "planned", "expense")));
        // O mês de destino tem as linhas copiadas com o valor original igual ao copiado; nenhum lançamento, previsão ou transferência nasce da cópia.
        Assert.Equal(before, await FinancialCountsAsync());
        var income = Item(plan.GetProperty("lines"), l => l.GetProperty("kind").GetString() == "Income");
        Assert.Equal(to, Text(income, "competence"));
        Assert.StartsWith(to, income.GetProperty("expectedDate").GetString());
        var history = await GetAsync(s, $"/history?competence={to}");
        Assert.Equal(3, history.GetArrayLength());
        Assert.All(history.EnumerateArray(), e => { Assert.Equal("Copied", e.GetProperty("action").GetString()); Assert.Equal("Copiado de " + from, e.GetProperty("note").GetString()); });
        // O mês de origem não muda.
        Assert.Equal("5000.00", Text(await PlanAsync(s, from, "all"), "planned", "income"));
    }

    [Fact]
    public async Task CopyingNeverOverwritesADestinationThatAlreadyHasLinesAndSkipsInactiveAccountsAndExistingLimits()
    {
        var s = await ArrangeAsync("mp-copy-guard");
        var from = Month(ThisMonth); var to = Month(ThisMonth.AddMonths(1));
        await LineAsync(s, Expense(from, "100.00", s.Pf));
        await LineAsync(s, Expense(from, "200.00", s.Pj, null, "Despesa da PJ"));
        await LimitAsync(s, from, s.Food, "900.00");
        await LimitAsync(s, from, s.Housing, "1200.00");
        await LimitAsync(s, to, s.Food, "1000.00");
        await using (var db = Db()) await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE financial_accounts SET is_active = false WHERE id = {s.Pj}");

        var copy = await ReadJsonAsync(await SendWithCsrfAsync(HttpMethod.Post, s.Plan + "/copy", s.Session, new { fromCompetence = from, toCompetence = to, confirm = true }));
        var plan = await PlanAsync(s, to);
        var second = await ReadJsonAsync(await SendWithCsrfAsync(HttpMethod.Post, s.Plan + "/copy", s.Session, new { fromCompetence = from, toCompetence = to, confirm = true }), HttpStatusCode.Conflict);

        // A linha da PJ inativa e o limite de Alimentação (já existente no destino, com outro valor) ficam de fora; nada é sobrescrito.
        Assert.Equal((1, 1, 1, 1), (copy.GetProperty("linesCopied").GetInt32(), copy.GetProperty("linesSkipped").GetInt32(), copy.GetProperty("limitsCopied").GetInt32(), copy.GetProperty("limitsSkipped").GetInt32()));
        Assert.Equal("1000.00", Text(Item(plan.GetProperty("categoryLimits"), l => l.GetProperty("categoryName").GetString() == "Alimentação"), "amount"));
        Assert.Equal("1200.00", Text(Item(plan.GetProperty("categoryLimits"), l => l.GetProperty("categoryName").GetString() == "Moradia"), "amount"));
        Assert.Contains("já tem linhas", second.GetProperty("title").GetString());
        // Mesma competência e origem sem planejamento são recusadas.
        await ReadJsonAsync(await SendWithCsrfAsync(HttpMethod.Post, s.Plan + "/copy", s.Session, new { fromCompetence = from, toCompetence = from, confirm = true }), HttpStatusCode.BadRequest);
        await ReadJsonAsync(await SendWithCsrfAsync(HttpMethod.Post, s.Plan + "/copy", s.Session, new { fromCompetence = Month(ThisMonth.AddMonths(5)), toCompetence = Month(ThisMonth.AddMonths(6)), confirm = true }), HttpStatusCode.BadRequest);
    }

    // ---- 8–9: PF/PJ e consolidação ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PlanningIsSeparatedByAccountAndConsolidatedWithoutLeakingBetweenPfAndPj()
    {
        var s = await ArrangeAsync("mp-scopes");
        var month = Month(ThisMonth);
        await LineAsync(s, Income(month, "9000.00", s.Pj, "Prestação de serviços"));
        await LineAsync(s, Income(month, "300.00", s.Pf, "Aluguel recebido"));
        await LineAsync(s, Income(month, "50.00", null, "Receita sem conta"));
        await LineAsync(s, Expense(month, "2000.00", s.Pf, s.Housing));
        await LineAsync(s, Expense(month, "800.00", s.Pj, null, "Contador"));

        var pf = await PlanAsync(s, scope: "primary");
        var pj = await PlanAsync(s, scope: "account", account: s.Pj);
        var all = await PlanAsync(s, scope: "all");
        var dashboardPj = await DashboardAsync(s, scope: "account", account: s.Pj);

        Assert.Equal(("300.00", "2000.00", 2), (Text(pf, "planned", "income"), Text(pf, "planned", "expense"), pf.GetProperty("lines").GetArrayLength()));
        Assert.Equal(("9000.00", "800.00", 2), (Text(pj, "planned", "income"), Text(pj, "planned", "expense"), pj.GetProperty("lines").GetArrayLength()));
        Assert.Equal(("9350.00", "2800.00", 5), (Text(all, "planned", "income"), Text(all, "planned", "expense"), all.GetProperty("lines").GetArrayLength()));
        Assert.Equal("PJ", Text(Item(pj.GetProperty("lines"), l => l.GetProperty("kind").GetString() == "Income"), "accountScope"));
        // O Dashboard da conta PJ usa o planejado da PJ.
        Assert.Equal(("9000.00", "800.00"), (Text(dashboardPj, "income", "planned"), Text(dashboardPj, "expenses", "consumption", "planned")));
    }

    [Fact]
    public async Task APlannedTransferBetweenPfAndPjIsNeitherIncomeNorExpenseAndNeverInventedFromPjRevenue()
    {
        var s = await ArrangeAsync("mp-transfer");
        var month = Month(ThisMonth);
        await LineAsync(s, Income(month, "9000.00", s.Pj, "Serviços PJ"));
        var before = await FinancialCountsAsync();

        // Receita PJ sozinha não cria transferência: nada é presumido.
        var none = await PlanAsync(s, scope: "all");
        var transfer = await LineAsync(s, new { competence = month, kind = "Transfer", description = "Pró-labore para a PF", accountId = s.Pj, destinationAccountId = s.Pf, amount = "4000.00", expectedDate = ThisMonth.AddDays(9) });
        var all = await PlanAsync(s, scope: "all");
        var pf = await PlanAsync(s, scope: "primary");
        var pj = await PlanAsync(s, scope: "account", account: s.Pj);
        var dashboard = await DashboardAsync(s, scope: "all");

        Assert.Equal(0, none.GetProperty("planned").GetProperty("transferCount").GetInt32());
        Assert.Equal(("Itaú PJ", "Itaú PF", "Transfer"), (Text(transfer, "accountName"), Text(transfer, "destinationAccountName"), Text(transfer, "kind")));
        // Consolidado: a transferência não soma à receita nem à despesa; cada conta vê a sua perna.
        Assert.Equal(("9000.00", "4000.00", "4000.00"), (Text(all, "planned", "income"), Text(all, "planned", "transfersOut"), Text(all, "planned", "transfersIn")));
        Assert.True(IsNull(all, "planned", "expense"));
        Assert.Equal(("0.00", "4000.00"), (Text(pf, "planned", "transfersOut"), Text(pf, "planned", "transfersIn")));
        Assert.Equal(("4000.00", "0.00"), (Text(pj, "planned", "transfersOut"), Text(pj, "planned", "transfersIn")));
        Assert.Equal("9000.00", Text(dashboard, "income", "planned"));
        // Nenhuma transferência ou lançamento realizado foi criado.
        Assert.Equal(before, await FinancialCountsAsync());
    }

    [Fact]
    public async Task ARealizedTransferIsNotConsolidatedIncomeOrExpenseInThePlanExecution()
    {
        var s = await ArrangeAsync("mp-transfer-real");
        var month = Month(ThisMonth);
        await LineAsync(s, Income(month, "1000.00", s.Pj));
        await TransactionAsync(s, s.Pj, "Income", "1000.00", Today);
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(s, "transfers", new { sourceAccountId = s.Pj, destinationAccountId = s.Pf, amount = "700.00", transferDate = Today, description = "PJ para PF" })).StatusCode);

        var all = await PlanAsync(s, scope: "all");
        var pj = await PlanAsync(s, scope: "account", account: s.Pj);
        var pf = await PlanAsync(s, scope: "primary");

        Assert.Equal(("1000.00", "0.00"), (Text(all, "execution", "income", "realized"), Text(all, "execution", "consumption", "realized")));
        Assert.Equal("1000.00", Text(pj, "execution", "income", "realized"));
        Assert.Equal("0.00", Text(pf, "execution", "income", "realized"));
    }

    // ---- 10–11: receita por hora, feriados e dias não trabalhados -------------------------------------------------------------------------

    [Fact]
    public async Task HourlyIncomeIsCalculatedByTheBackendFromRateHoursAndBillableDays()
    {
        var s = await ArrangeAsync("mp-hourly");

        var preview = await GetAsync(s, $"/hourly-income?competence={Fixed}&hourlyRate=150.00&hoursPerDay=8&billableDays=20");
        var line = await LineAsync(s, new { competence = Fixed, kind = "Income", nature = "Recurring", description = "Serviços PJ por hora", accountId = s.Pj, hourlyRate = "150.00", hoursPerDay = "8", billableDays = 20, expectedDate = "2031-12-10" });
        var plan = await PlanAsync(s, Fixed, "account", s.Pj);

        Assert.Equal(("24000.00", 23, 20), (Text(preview, "amount"), preview.GetProperty("businessDays").GetInt32(), preview.GetProperty("billableDays").GetInt32()));
        Assert.Equal("24000.00", Text(line, "amount"));
        Assert.Equal(("150.00", "8", 20, 23, false), (Text(line, "hourly", "hourlyRate"), Text(line, "hourly", "hoursPerDay"), line.GetProperty("hourly").GetProperty("billableDays").GetInt32(), line.GetProperty("hourly").GetProperty("businessDays").GetInt32(), line.GetProperty("hourly").GetProperty("exceedsCalendar").GetBoolean()));
        Assert.Equal("24000.00", Text(plan, "planned", "income"));
        Assert.Equal("2031-12-10", line.GetProperty("expectedDate").GetString());
    }

    [Fact]
    public async Task HourlyIncomeRejectsMoreBillableDaysThanTheCalendarAnInformedAmountThatDisagreesAndNonIncomeLines()
    {
        var s = await ArrangeAsync("mp-hourly-guard");
        object Hourly(object? amount = null, int days = 20, string kind = "Income") => new { competence = Fixed, kind, nature = "Variable", description = "Horas", accountId = s.Pj, hourlyRate = "100.00", hoursPerDay = "8", billableDays = days, amount };

        var tooMany = await LineAsync(s, Hourly(days: 24), HttpStatusCode.BadRequest);
        var mismatch = await LineAsync(s, Hourly(amount: "1.00"), HttpStatusCode.BadRequest);
        var matching = await LineAsync(s, Hourly(amount: "16000.00"));
        var expense = await LineAsync(s, Hourly(kind: "Expense"), HttpStatusCode.BadRequest);
        await LineAsync(s, new { competence = Fixed, kind = "Income", nature = "Variable", description = "Horas", accountId = s.Pj, hourlyRate = "100.00", hoursPerDay = "8" }, HttpStatusCode.BadRequest);

        Assert.Contains("23 dia(s) útil(eis)", tooMany.GetProperty("errors").GetProperty("billableDays")[0].GetString());
        Assert.True(mismatch.GetProperty("errors").TryGetProperty("amount", out _));
        Assert.Equal("16000.00", Text(matching, "amount"));
        Assert.True(expense.GetProperty("errors").TryGetProperty("hourlyRate", out _));
    }

    [Fact]
    public async Task HolidaysAndNonWorkingDaysReduceTheBusinessDaysAndTheCalendarIsConfigurable()
    {
        var s = await ArrangeAsync("mp-holidays");

        var empty = await GetAsync(s, $"/working-days?competence={Fixed}");
        var holiday = await ReadJsonAsync(await SendWithCsrfAsync(HttpMethod.Post, s.Plan + "/non-working-days", s.Session, new { date = "2031-12-25", kind = "Holiday", description = "Natal" }), HttpStatusCode.Created);
        await ReadJsonAsync(await SendWithCsrfAsync(HttpMethod.Post, s.Plan + "/non-working-days", s.Session, new { date = "2031-12-26", kind = "NonWorking", description = "Recesso" }), HttpStatusCode.Created);
        var weekend = await ReadJsonAsync(await SendWithCsrfAsync(HttpMethod.Post, s.Plan + "/non-working-days", s.Session, new { date = "2031-12-20", kind = "NonWorking", description = "Sábado" }), HttpStatusCode.Created);
        var duplicate = await SendWithCsrfAsync(HttpMethod.Post, s.Plan + "/non-working-days", s.Session, new { date = "2031-12-25", kind = "Holiday", description = "Natal de novo" });
        var configured = await GetAsync(s, $"/working-days?competence={Fixed}");
        // Com o calendário configurado, o limite de dias faturáveis passa a ser 21 e a receita acima dele é recusada.
        var tooMany = await LineAsync(s, new { competence = Fixed, kind = "Income", nature = "Recurring", description = "Horas", accountId = s.Pj, hourlyRate = "100.00", hoursPerDay = "8", billableDays = 22 }, HttpStatusCode.BadRequest);
        var allowed = await LineAsync(s, new { competence = Fixed, kind = "Income", nature = "Recurring", description = "Horas", accountId = s.Pj, hourlyRate = "100.00", hoursPerDay = "8", billableDays = 21 });
        var removed = await ReadJsonAsync(await SendWithCsrfAsync(HttpMethod.Delete, $"{s.Plan}/non-working-days/{Text(holiday, "id")}?competence={Fixed}", s.Session));

        Assert.Equal((31, 23, 0, 23), (empty.GetProperty("calendarDays").GetInt32(), empty.GetProperty("weekdays").GetInt32(), empty.GetProperty("nonWorkingWeekdays").GetInt32(), empty.GetProperty("businessDays").GetInt32()));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.True(weekend.GetProperty("weekend").GetBoolean());
        Assert.Equal((23, 2, 21, 3), (configured.GetProperty("weekdays").GetInt32(), configured.GetProperty("nonWorkingWeekdays").GetInt32(), configured.GetProperty("businessDays").GetInt32(), configured.GetProperty("nonWorkingDays").GetArrayLength()));
        Assert.Contains("21 dia(s) útil(eis)", tooMany.GetProperty("errors").GetProperty("billableDays")[0].GetString());
        Assert.Equal("16800.00", Text(allowed, "amount"));
        Assert.Equal(22, removed.GetProperty("businessDays").GetInt32());
        // Com 22 dias úteis a linha de 21 dias cabe; cadastrar mais dois dias não trabalhados depois da linha criada a deixa acima do calendário, sinalizada como pendência.
        Assert.False((await PlanAsync(s, Fixed, "account", s.Pj)).GetProperty("lines")[0].GetProperty("hourly").GetProperty("exceedsCalendar").GetBoolean());
        await ReadJsonAsync(await SendWithCsrfAsync(HttpMethod.Post, s.Plan + "/non-working-days", s.Session, new { date = "2031-12-22", kind = "NonWorking", description = "Recesso" }), HttpStatusCode.Created);
        await ReadJsonAsync(await SendWithCsrfAsync(HttpMethod.Post, s.Plan + "/non-working-days", s.Session, new { date = "2031-12-23", kind = "NonWorking", description = "Recesso" }), HttpStatusCode.Created);
        var plan = await PlanAsync(s, Fixed, "account", s.Pj);
        Assert.True(plan.GetProperty("lines")[0].GetProperty("hourly").GetProperty("exceedsCalendar").GetBoolean());
        Assert.Contains(plan.GetProperty("pendencies").EnumerateArray(), p => p.GetProperty("code").GetString() == "HourlyIncomeAboveCalendar");
    }

    [Fact]
    public async Task CopyingAnHourlyIncomeRecalculatesItFromTheCalendarOfTheDestinationMonth()
    {
        var s = await ArrangeAsync("mp-copy-hourly");
        await LineAsync(s, new { competence = "2031-12", kind = "Income", nature = "Recurring", description = "Horas", accountId = s.Pj, hourlyRate = "100.00", hoursPerDay = "8", billableDays = 22 });
        // Fevereiro de 2032 (ano bissexto) tem 20 dias de segunda a sexta: os 22 dias faturáveis de origem são limitados a 20.
        var copy = await ReadJsonAsync(await SendWithCsrfAsync(HttpMethod.Post, s.Plan + "/copy", s.Session, new { fromCompetence = "2031-12", toCompetence = "2032-02", confirm = true }));
        var plan = await PlanAsync(s, "2032-02", "account", s.Pj);

        Assert.Equal(1, copy.GetProperty("recalculatedHourlyLines").GetArrayLength());
        var line = plan.GetProperty("lines")[0];
        Assert.Equal(("16000.00", 20, 20), (Text(line, "amount"), line.GetProperty("hourly").GetProperty("billableDays").GetInt32(), line.GetProperty("hourly").GetProperty("businessDays").GetInt32()));
    }

    // ---- 12–14: recorrências, cartões, parcelas e meta de cartões ----------------------------------------------------------------------------

    [Fact]
    public async Task ARecurringExpenseIsATargetInThePlanAndTheMaterializedCommitmentsComeFromTheProjectionWithoutDuplicates()
    {
        var s = await ArrangeAsync("mp-recurring");
        var next = ThisMonth.AddMonths(1);
        await LineAsync(s, Expense(Month(next), "300.00", s.Pf, s.Expense, "Assinaturas", "Recurring"));
        var recurrence = (await ReadAsync<IdDto>(await PostAsync(s, "recurrences", new { accountId = s.Pf, categoryId = s.Expense, type = "Expense", description = "Assinatura", amount = "300.00", frequency = "Monthly", interval = 1, startDate = next, occurrenceCount = 3 }))).Id;
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(s, $"recurrences/{recurrence}/materialize", new { from = next, through = next.AddMonths(2).AddDays(27) })).StatusCode);

        var six = await SixMonthsAsync(s, Month(next));
        var first = six.GetProperty("months")[0];

        // O planejado é o do plano (300), a obrigação conhecida da recorrência materializada está no projetado (300) uma única vez e o plano não a duplica.
        Assert.Equal(("300.00", "300.00", "300.00"), (Text(first, "plannedExpense"), Text(first, "projectedExpense"), Text(first, "knownObligations")));
        Assert.Equal("MonthlyPlan", Text(first, "plannedExpenseSource"));
        var second = six.GetProperty("months")[1];
        Assert.True(IsNull(second, "plannedExpense"));
        Assert.Equal("300.00", Text(second, "projectedExpense"));
    }

    private async Task<(Guid Card, string Competence)> CardAsync(Setup s, Guid payer, string name, string amount, int installments = 1)
    {
        var card = (await ReadAsync<IdDto>(await PostAsync(s, "credit-cards", new { name, issuer = "Banco", creditLimit = "20000.00", closingDay = 5, dueDay = 15, defaultPaymentAccountId = payer }))).Id;
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(s, $"credit-cards/{card}/purchases", new { description = "Compra sintética", purchaseDate = Today, totalAmount = amount, installmentCount = installments, categoryId = s.Expense })).StatusCode);
        await using var db = Db();
        var invoice = await db.FinancialCreditCardInvoices.AsNoTracking().Where(i => i.CreditCardId == card).OrderBy(i => i.DueDate).FirstAsync();
        return (card, invoice.Competence);
    }

    [Fact]
    public async Task CardInstallmentsComposeTheProjectedConsumptionOfEachInvoiceMonthAndThePaymentNeverDuplicatesIt()
    {
        var s = await ArrangeAsync("mp-cards");
        var (_, competence) = await CardAsync(s, s.Pf, "Cartão PF", "300.00", 3);

        var six = await SixMonthsAsync(s, competence);
        var months = six.GetProperty("months").EnumerateArray().Take(4).ToList();

        // Três parcelas de R$ 100 nas três faturas seguintes; o pagamento de fatura (caixa) não entra de novo no consumo.
        Assert.Equal(["100.00", "100.00", "100.00", "0.00"], months.Select(m => Text(m, "projectedExpense")).ToArray());
        // Cada parcela já lançada é consumo confirmado na competência da sua fatura (a mesma regra do Dashboard): o mês seguinte mostra o mesmo número nas duas visões.
        Assert.Equal(["100.00", "100.00", "100.00", "0.00"], months.Select(m => Text(m, "realizedExpense")).ToArray());
        var nextMonth = await DashboardAsync(s, Text(months[1], "competence"));
        Assert.Equal((Text(months[1], "realizedExpense"), Text(months[1], "projectedExpense")), (Text(nextMonth, "expenses", "consumption", "realized"), Text(nextMonth, "expenses", "consumption", "projected")));
        Assert.Equal(competence, Text(months[0], "competence"));
        // O Dashboard do mês da primeira fatura concorda com a visão de seis meses.
        var dashboard = await DashboardAsync(s, competence);
        Assert.Equal(Text(months[0], "projectedExpense"), Text(dashboard, "expenses", "consumption", "projected"));
    }

    [Fact]
    public async Task TheBudgetCardTargetIsTheSingleSourceAndPlanningNeverCreatesOrCopiesAnEquivalentTarget()
    {
        var s = await ArrangeAsync("mp-card-target");
        var month = Month(ThisMonth);
        var budget = await SendWithCsrfAsync(HttpMethod.Post, $"{s.Root}/budgets", s.Session, new { competence = month });
        var budgetId = (await ReadAsync<IdDto>(budget)).Id;
        var saved = await SendWithCsrfAsync(HttpMethod.Post, $"{s.Root}/budgets/{budgetId}/targets", s.Session, new { groupingType = "AllCards", plannedAmount = "5000.00" });
        Assert.Equal(HttpStatusCode.Created, saved.StatusCode);

        await LineAsync(s, Expense(month, "1000.00", s.Pf));
        await LimitAsync(s, month, s.Food, "2500.00");
        Assert.Equal(HttpStatusCode.OK, (await SendWithCsrfAsync(HttpMethod.Post, s.Plan + "/copy", s.Session, new { fromCompetence = month, toCompetence = Month(ThisMonth.AddMonths(1)), confirm = true })).StatusCode);
        var plan = await PlanAsync(s, scope: "all");
        var next = await PlanAsync(s, Month(ThisMonth.AddMonths(1)), "all");

        // A meta "Todos os cartões" é a do orçamento do mês (R$ 5.000), independente das linhas e dos limites por categoria; a cópia do plano não a replica
        // para o mês seguinte nem cria outra: cada mês decide o seu teto.
        Assert.Equal(("5000.00", "BudgetTarget"), (Text(plan, "execution", "cards", "target"), Text(plan, "execution", "cards", "targetSource")));
        Assert.Equal("NotDefined", Text(next, "execution", "cards", "plannedState"));
        Assert.Equal(1, (await FinancialCountsAsync()).Targets);
    }

    // ---- 15–17: planejado × projetado × realizado ------------------------------------------------------------------------------------------

    [Fact]
    public async Task PlannedProjectedAndRealizedComeFromTheSameBaseAndAPendingForecastIsNotCountedTwice()
    {
        var s = await ArrangeAsync("mp-pvr");
        var month = Month(ThisMonth);
        var due = new DateOnly(Today.Year, Today.Month, 28);
        await LineAsync(s, Income(month, "1000.00", s.Pf));
        await TransactionAsync(s, s.Pf, "Income", "400.00", Today);
        var forecast = await PlannedAsync(s, s.Pf, "Income", "300.00", due);

        var before = await PlanAsync(s);
        // A previsão é realizada: passa a ser um lançamento efetivo e deixa de ser pendente (não soma duas vezes).
        var realize = await PostAsync(s, $"planned-transactions/{forecast}/realize", new { amount = "300.00", transactionDate = Today });
        Assert.Equal(HttpStatusCode.Created, realize.StatusCode);
        var after = await PlanAsync(s);

        var incomeBefore = before.GetProperty("execution").GetProperty("income");
        var incomeAfter = after.GetProperty("execution").GetProperty("income");
        Assert.Equal(("1000.00", "400.00", "700.00", "300.00"), (Text(incomeBefore, "planned"), Text(incomeBefore, "realized"), Text(incomeBefore, "projected"), Text(incomeBefore, "pending")));
        Assert.Equal(("700.00", "700.00", "0.00"), (Text(incomeAfter, "realized"), Text(incomeAfter, "projected"), Text(incomeAfter, "pending")));
        Assert.Equal(("-600.00", "-300.00"), (Text(incomeBefore, "realizedDifference"), Text(incomeBefore, "projectedDifference")));
        Assert.Equal("-300.00", Text(incomeAfter, "projectedDifference"));
        // O valor planejado não é reescrito pela realização.
        Assert.Equal("1000.00", Text(incomeAfter, "planned"));
    }

    // ---- 18: seis meses ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheSixMonthViewListsTheSelectedMonthAndTheNextFiveWithPlannedProjectedRealizedAndRisk()
    {
        var s = await ArrangeAsync("mp-six");
        var m0 = Month(ThisMonth); var m2 = Month(ThisMonth.AddMonths(2));
        await LineAsync(s, Income(m0, "5000.00", s.Pf));
        await LineAsync(s, Expense(m0, "1500.00", s.Pf));
        await LineAsync(s, Expense(m2, "800.00", s.Pf));
        await TransactionAsync(s, s.Pf, "Income", "2000.00", Today);
        await TransactionAsync(s, s.Pf, "Expense", "100.00", Today);
        await PlannedAsync(s, s.Pf, "Expense", "5000.00", ThisMonth.AddMonths(2).AddDays(9));

        var six = await SixMonthsAsync(s);
        var months = six.GetProperty("months").EnumerateArray().ToList();

        Assert.Equal(6, months.Count);
        Assert.Equal((m0, Month(ThisMonth.AddMonths(5))), (Text(six, "from"), Text(six, "through")));
        Assert.Equal(Enumerable.Range(0, 6).Select(i => Month(ThisMonth.AddMonths(i))).ToArray(), months.Select(m => Text(m, "competence")).ToArray());
        var first = months[0];
        Assert.Equal(("5000.00", "1500.00", "3500.00"), (Text(first, "plannedIncome"), Text(first, "plannedExpense"), Text(first, "plannedResult")));
        Assert.Equal(("2000.00", "100.00", "1900.00"), (Text(first, "realizedIncome"), Text(first, "realizedExpense"), Text(first, "realizedResult")));
        Assert.Equal(("MonthlyPlan", "Current"), (Text(first, "plannedIncomeSource"), Text(first, "periodKind")));
        // Mês sem plano: planejado nulo, nunca zero; mês com despesa projetada acima do plano e saldo negativo é o de maior risco.
        Assert.True(IsNull(months[1], "plannedIncome") && IsNull(months[1], "plannedExpense") && IsNull(months[1], "plannedResult"));
        Assert.Equal(("800.00", "5000.00"), (Text(months[2], "plannedExpense"), Text(months[2], "projectedExpense")));
        Assert.NotEqual("Low", Text(months[2], "risk"));
        Assert.Contains(m2, six.GetProperty("summary").GetProperty("highestRiskMonths").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(4, six.GetProperty("summary").GetProperty("monthsWithoutPlan").GetInt32());
        Assert.True(IsNull(six, "summary", "plannedResult"));
        Assert.All(months, m => Assert.Contains(Text(m, "coverage", "obligationsCoverage"), new[] { "Covered", "Shortfall", "Unknown" }));
    }

    [Fact]
    public async Task TheSixMonthViewRespectsTheSelectedAccountAndAnInvalidRequestIsRejected()
    {
        var s = await ArrangeAsync("mp-six-scope");
        var month = Month(ThisMonth);
        await LineAsync(s, Income(month, "9000.00", s.Pj));
        await LineAsync(s, Income(month, "100.00", s.Pf));

        var pj = await SixMonthsAsync(s, month, "account", s.Pj);
        var pf = await SixMonthsAsync(s, month);
        var all = await SixMonthsAsync(s, month, "all");

        Assert.Equal(("9000.00", "100.00", "9100.00"), (Text(pj.GetProperty("months")[0], "plannedIncome"), Text(pf.GetProperty("months")[0], "plannedIncome"), Text(all.GetProperty("months")[0], "plannedIncome")));
        Assert.Equal(s.Pj.ToString(), Text(pj, "scope", "accountId"));
        await GetAsync(s, "/six-months?from=2026-13", HttpStatusCode.BadRequest);
        await GetAsync(s, "/six-months?scope=todas", HttpStatusCode.BadRequest);
        await GetAsync(s, "/six-months?scope=account", HttpStatusCode.BadRequest);
        await GetAsync(s, $"/six-months?scope=account&accountId={Guid.NewGuid()}", HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SixMonthsReportsPastCurrentAndFutureNeverTheCalendarHistoricalLabel()
    {
        var s = await ArrangeAsync("mp-period-kind");
        var plan = await SixMonthsAsync(s, Month(ThisMonth.AddMonths(-2)));
        var kinds = plan.GetProperty("months").EnumerateArray().Select(m => Text(m, "periodKind")).ToArray();
        Assert.Equal(new[] { "Past", "Past", "Current", "Future", "Future", "Future" }, kinds); // o painel da Visão Geral só conhece estes três valores
    }

    // ---- 20: conta inativa com histórico ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnInactiveAccountKeepsItsPlanningHistoryButAcceptsNoNewLines()
    {
        var s = await ArrangeAsync("mp-inactive");
        var month = Month(ThisMonth);
        await LineAsync(s, Expense(month, "250.00", s.Pj, null, "Despesa da PJ"));
        await using (var db = Db()) await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE financial_accounts SET is_active = false WHERE id = {s.Pj}");

        var plan = await PlanAsync(s, scope: "account", account: s.Pj);
        var all = await PlanAsync(s, scope: "all");
        var rejected = await LineAsync(s, Expense(month, "10.00", s.Pj), HttpStatusCode.BadRequest);
        var edit = await ReviseAsync(s, Text(plan.GetProperty("lines")[0], "id"), Expense(month, "260.00", s.Pj, null, "Despesa da PJ"));

        // A conta inativa continua escolhível enquanto tem planejamento, o histórico permanece e a linha existente pode ser revisada; linhas novas não.
        Assert.Equal("250.00", Text(plan, "planned", "expense"));
        Assert.False(plan.GetProperty("lines")[0].GetProperty("accountActive").GetBoolean());
        Assert.Contains(all.GetProperty("scope").GetProperty("accounts").EnumerateArray(), a => a.GetProperty("accountId").GetString() == s.Pj.ToString() && !a.GetProperty("isActive").GetBoolean());
        Assert.Contains(plan.GetProperty("pendencies").EnumerateArray(), p => p.GetProperty("code").GetString() == "LineOnInactiveAccount");
        Assert.True(rejected.GetProperty("errors").TryGetProperty("accountId", out _));
        Assert.Equal("260.00", Text(edit, "amount"));
    }

    // ---- 21: precisão monetária ------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("0.01", HttpStatusCode.Created)]
    [InlineData("1234567.89", HttpStatusCode.Created)]
    [InlineData("9999999999999.99", HttpStatusCode.Created)]
    [InlineData("0.00", HttpStatusCode.BadRequest)]
    [InlineData("-5.00", HttpStatusCode.BadRequest)]
    [InlineData("10.005", HttpStatusCode.BadRequest)]
    [InlineData("1500,50", HttpStatusCode.BadRequest)]
    [InlineData("1.500.00", HttpStatusCode.BadRequest)]
    [InlineData("abc", HttpStatusCode.BadRequest)]
    [InlineData("10000000000000.00", HttpStatusCode.BadRequest)]
    public async Task MonetaryAmountsAreExactDecimalStringsWithAtMostTwoPlacesAndADotSeparator(string amount, HttpStatusCode expected)
    {
        var s = await ArrangeAsync("mp-money-" + Math.Abs(amount.GetHashCode()));

        var response = await SendWithCsrfAsync(HttpMethod.Post, s.Plan + "/lines", s.Session, Income(Month(ThisMonth), amount, s.Pf));

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.Created) Assert.Equal(amount, Text(await ReadJsonAsync(response, expected), "amount"));
    }

    [Fact]
    public async Task LimitsRejectNegativeAndFractionalCentsAndAcceptZero()
    {
        var s = await ArrangeAsync("mp-limit-money");
        var month = Month(ThisMonth);

        await LimitAsync(s, month, s.Food, "-1.00", expected: HttpStatusCode.BadRequest);
        await LimitAsync(s, month, s.Food, "10.001", expected: HttpStatusCode.BadRequest);
        await LimitAsync(s, month, s.Food, "10,00", expected: HttpStatusCode.BadRequest);
        Assert.Equal("0.00", Text(await LimitAsync(s, month, s.Food, "0.00"), "amount"));
        // Limites só aceitam categorias de despesa existentes.
        await LimitAsync(s, month, s.Income, "10.00", expected: HttpStatusCode.BadRequest);
        await LimitAsync(s, month, Guid.NewGuid(), "10.00", expected: HttpStatusCode.NotFound);
    }

    // ---- Validações -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task InvalidLinesAreRejectedWithFieldErrors()
    {
        var s = await ArrangeAsync("mp-validate");
        var month = Month(ThisMonth);
        async Task<JsonElement> Bad(object body) => await LineAsync(s, body, HttpStatusCode.BadRequest);

        Assert.True((await Bad(new { competence = "2026-13", kind = "Income", nature = "Fixed", description = "x", amount = "1.00" })).GetProperty("errors").TryGetProperty("competence", out _));
        Assert.True((await Bad(new { competence = month, kind = "Outro", nature = "Fixed", description = "x", amount = "1.00" })).GetProperty("errors").TryGetProperty("kind", out _));
        Assert.True((await Bad(new { competence = month, kind = "Expense", nature = "Mensal", description = "x", amount = "1.00" })).GetProperty("errors").TryGetProperty("nature", out _));
        Assert.True((await Bad(new { competence = month, kind = "Expense", nature = "Fixed", description = "  ", amount = "1.00" })).GetProperty("errors").TryGetProperty("description", out _));
        Assert.True((await Bad(new { competence = month, kind = "Expense", nature = "Fixed", description = new string('x', 201), amount = "1.00" })).GetProperty("errors").TryGetProperty("description", out _));
        // Categoria de tipo diferente do da linha e data fora da competência.
        Assert.True((await Bad(new { competence = month, kind = "Expense", nature = "Fixed", description = "x", categoryId = s.Income, amount = "1.00" })).GetProperty("errors").TryGetProperty("categoryId", out _));
        Assert.True((await Bad(new { competence = month, kind = "Income", nature = "Fixed", description = "x", categoryId = s.Expense, amount = "1.00" })).GetProperty("errors").TryGetProperty("categoryId", out _));
        Assert.True((await Bad(new { competence = month, kind = "Income", nature = "Fixed", description = "x", amount = "1.00", expectedDate = ThisMonth.AddMonths(1) })).GetProperty("errors").TryGetProperty("expectedDate", out _));
        // Transferência: duas contas diferentes, sem natureza nem categoria.
        Assert.True((await Bad(new { competence = month, kind = "Transfer", description = "x", accountId = s.Pf, destinationAccountId = s.Pf, amount = "1.00" })).GetProperty("errors").TryGetProperty("destinationAccountId", out _));
        Assert.True((await Bad(new { competence = month, kind = "Transfer", description = "x", accountId = s.Pf, amount = "1.00" })).GetProperty("errors").TryGetProperty("destinationAccountId", out _));
        Assert.True((await Bad(new { competence = month, kind = "Transfer", nature = "Fixed", description = "x", accountId = s.Pf, destinationAccountId = s.Pj, amount = "1.00" })).GetProperty("errors").TryGetProperty("nature", out _));
        Assert.True((await Bad(new { competence = month, kind = "Expense", nature = "Fixed", description = "x", accountId = s.Pf, destinationAccountId = s.Pj, amount = "1.00" })).GetProperty("errors").TryGetProperty("destinationAccountId", out _));
        // O tipo de uma linha não muda na revisão.
        var line = await LineAsync(s, Income(month, "10.00", s.Pf));
        Assert.True((await ReviseAsync(s, Text(line, "id"), Expense(month, "10.00", s.Pf), HttpStatusCode.BadRequest)).GetProperty("errors").TryGetProperty("kind", out _));
        await GetAsync(s, "?competence=2026-13", HttpStatusCode.BadRequest);
        await GetAsync(s, "?scope=todas", HttpStatusCode.BadRequest);
        await GetAsync(s, "/history?competence=2026", HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task WritesRequireTheCsrfTokenAndReadsNeverWrite()
    {
        var s = await ArrangeAsync("mp-csrf");
        var month = Month(ThisMonth);

        var withoutToken = await SendJsonAsync(HttpMethod.Post, s.Plan + "/lines", s.Session, null, Income(month, "10.00", s.Pf));
        var limitWithoutToken = await SendJsonAsync(HttpMethod.Put, s.Plan + "/category-limits", s.Session, null, new { competence = month, categoryId = s.Food, amount = "10.00" });
        var copyWithoutToken = await SendJsonAsync(HttpMethod.Post, s.Plan + "/copy", s.Session, null, new { fromCompetence = month, toCompetence = Month(ThisMonth.AddMonths(1)), confirm = true });
        await PlanAsync(s); await SixMonthsAsync(s); await GetAsync(s, $"/working-days?competence={month}");

        Assert.Equal([HttpStatusCode.BadRequest, HttpStatusCode.BadRequest, HttpStatusCode.BadRequest], [withoutToken.StatusCode, limitWithoutToken.StatusCode, copyWithoutToken.StatusCode]);
        await using var db = Db();
        Assert.Equal((0, 0, 0, 0), (await db.FinancialPlanLines.CountAsync(), await db.FinancialPlanHistory.CountAsync(), await db.FinancialBudgets.CountAsync(), await db.FinancialNonWorkingDays.CountAsync()));
    }

    // ---- 22: isolamento entre workspaces -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PlanningIsIsolatedBetweenWorkspacesAndAForeignUserOrAccountGetsNotFound()
    {
        var a = await ArrangeAsync("mp-iso-a");
        var b = await ArrangeAsync("mp-iso-b");
        var month = Month(ThisMonth);
        var line = await LineAsync(a, Income(month, "1000.00", a.Pf));
        await LimitAsync(a, month, a.Food, "100.00");
        var id = Text(line, "id");

        // O usuário B não enxerga nem altera o orçamento de A.
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, a.Plan + $"?competence={month}", b.Session)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, a.Plan + "/six-months", b.Session)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, a.Plan + $"/history?competence={month}", b.Session)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Post, a.Plan + "/lines", b.Session, Income(month, "10.00", a.Pf))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Put, $"{a.Plan}/lines/{id}", b.Session, Income(month, "10.00", a.Pf))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Delete, $"{a.Plan}/lines/{id}", b.Session)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Put, a.Plan + "/category-limits", b.Session, new { competence = month, categoryId = a.Food, amount = "1.00" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Post, a.Plan + "/copy", b.Session, new { fromCompetence = month, toCompetence = Month(ThisMonth.AddMonths(1)), confirm = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Post, a.Plan + "/non-working-days", b.Session, new { date = "2031-12-25", kind = "Holiday", description = "x" })).StatusCode);
        // Dentro do próprio orçamento, o id de uma linha do orçamento A não existe; e contas ou categorias de A não servem ao orçamento B.
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Put, $"{b.Plan}/lines/{id}", b.Session, Income(month, "10.00", b.Pf))).StatusCode);
        var foreignAccount = await LineAsync(b, Income(month, "10.00", a.Pf), HttpStatusCode.BadRequest);
        Assert.True(foreignAccount.GetProperty("errors").TryGetProperty("accountId", out _));
        await LimitAsync(b, month, a.Food, "1.00", expected: HttpStatusCode.NotFound);
        // Nada vazou: B continua sem plano e A mantém o seu.
        Assert.False((await PlanAsync(b)).GetProperty("hasPlan").GetBoolean());
        Assert.Equal("1000.00", Text(await PlanAsync(a), "planned", "income"));
    }

    // ---- 24: consistência com Dashboard e Agenda ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task ThePlanNeverChangesTheAgendaAndTheSixMonthViewAgreesWithTheAgendaProjectionAndTheDashboard()
    {
        var s = await ArrangeAsync("mp-consistency");
        var month = Month(ThisMonth);
        await TransactionAsync(s, s.Pf, "Income", "800.00", Today);
        await TransactionAsync(s, s.Pf, "Expense", "120.00", Today);
        await PlannedAsync(s, s.Pf, "Expense", "60.00", new DateOnly(Today.Year, Today.Month, 28));
        await PlannedAsync(s, s.Pf, "Income", "200.00", ThisMonth.AddMonths(1).AddDays(4));
        var agendaBefore = await GetAsync(s.Session, $"{s.Root}/financial-planning/six-months?from={month}&accountId={s.Pf}");
        var calendarBefore = await GetAsync(s.Session, $"{s.Root}/financial-calendar?from={ThisMonth:yyyy-MM-dd}&through={ThisMonth.AddMonths(2):yyyy-MM-dd}&accountId={s.Pf}");

        await LineAsync(s, Income(month, "9999.00", s.Pf));
        await LineAsync(s, Expense(month, "8888.00", s.Pf));
        var agendaAfter = await GetAsync(s.Session, $"{s.Root}/financial-planning/six-months?from={month}&accountId={s.Pf}");
        var calendarAfter = await GetAsync(s.Session, $"{s.Root}/financial-calendar?from={ThisMonth:yyyy-MM-dd}&through={ThisMonth.AddMonths(2):yyyy-MM-dd}&accountId={s.Pf}");
        var six = await SixMonthsAsync(s, month);
        var plan = await PlanAsync(s);
        var dashboard = await DashboardAsync(s);

        // O plano não é movimentação: a Agenda (projeção de seis meses e calendário) é idêntica, e os seus totais são os do projetado do plano.
        Assert.Equal(agendaBefore.GetRawText().Replace(" ", ""), agendaAfter.GetRawText().Replace(" ", ""));
        Assert.Equal(calendarBefore.GetRawText(), calendarAfter.GetRawText());
        var agendaMonths = agendaAfter.GetProperty("months").EnumerateArray().ToList();
        var sixMonths = six.GetProperty("months").EnumerateArray().ToList();
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(Text(agendaMonths[i], "totalIncome"), Text(sixMonths[i], "projectedIncome"));
            Assert.Equal(Text(agendaMonths[i], "totalExpense"), Text(sixMonths[i], "projectedExpense"));
            Assert.Equal(Text(agendaMonths[i], "realizedIncome"), Text(sixMonths[i], "realizedIncome"));
            Assert.Equal(agendaMonths[i].GetProperty("closingBalance").ValueKind == JsonValueKind.Null ? null : Text(agendaMonths[i], "closingBalance"), sixMonths[i].GetProperty("closingBalance").ValueKind == JsonValueKind.Null ? null : Text(sixMonths[i], "closingBalance"));
        }
        // O Dashboard e a visão mensal usam o mesmo planejado e o mesmo projetado da visão de seis meses.
        Assert.Equal(Text(sixMonths[0], "plannedIncome"), Text(dashboard, "income", "planned"));
        Assert.Equal(Text(sixMonths[0], "plannedExpense"), Text(dashboard, "expenses", "consumption", "planned"));
        Assert.Equal(Text(sixMonths[0], "projectedExpense"), Text(dashboard, "expenses", "consumption", "projected"));
        Assert.Equal(Text(sixMonths[0], "projectedIncome"), Text(dashboard, "income", "projected"));
        Assert.Equal(Text(plan, "planned", "income"), Text(sixMonths[0], "plannedIncome"));
        Assert.Equal(Text(plan, "execution", "consumption", "projected"), Text(sixMonths[0], "projectedExpense"));
    }

    [Fact]
    public async Task PlanningNeverTouchesFinancialFactsBalancesOrOpeningBalances()
    {
        var s = await ArrangeAsync("mp-no-side-effects");
        var month = Month(ThisMonth);
        await TransactionAsync(s, s.Pf, "Expense", "50.00", Today);
        async Task<string> Fingerprint()
        {
            await using var db = Db();
            var accounts = await db.FinancialAccounts.AsNoTracking().OrderBy(a => a.Id).Select(a => $"{a.Id}|{a.OpeningBalance}|{a.OpeningBalanceDate}|{a.IsPrimary}|{a.IsActive}").ToListAsync();
            var transactions = await db.FinancialTransactions.AsNoTracking().OrderBy(t => t.Id).Select(t => $"{t.Id}|{t.Amount}|{t.CategoryId}|{t.IsCancelled}").ToListAsync();
            return string.Join(";", accounts) + "#" + string.Join(";", transactions) + "#" + await db.FinancialPlannedTransactions.CountAsync() + "#" + await db.FinancialTransfers.CountAsync()
                + "#" + await db.FinancialCreditCards.CountAsync() + "#" + await db.FinancialDebts.CountAsync();
        }
        var before = await Fingerprint();

        var line = await LineAsync(s, Expense(month, "100.00", s.Pf));
        await ReviseAsync(s, Text(line, "id"), Expense(month, "120.00", s.Pf));
        await LimitAsync(s, month, s.Food, "1.00");
        await SendWithCsrfAsync(HttpMethod.Post, s.Plan + "/copy", s.Session, new { fromCompetence = month, toCompetence = Month(ThisMonth.AddMonths(1)), confirm = true });
        await SendWithCsrfAsync(HttpMethod.Delete, $"{s.Plan}/lines/{Text(line, "id")}", s.Session);

        Assert.Equal(before, await Fingerprint());
    }
}
