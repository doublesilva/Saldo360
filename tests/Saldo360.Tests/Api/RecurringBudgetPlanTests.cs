using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.Common;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>
/// T12.1 (encerramento): (1) o Plano mensal lê os limites de categoria do resolvedor único — competências SEM orçamento mensal materializado mostram as metas herdadas, exceções substituem o
/// padrão, metas suspensas continuam suspensas e consultas não gravam nada; (2) restaurar ao padrão é um novo evento, idempotente, sem tocar no histórico. Meses relativos ao mês corrente.
/// </summary>
[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Budgets")]
[Trait("Dependency", "PostgreSQL")]
public sealed class RecurringBudgetPlanTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private static DateOnly ThisMonth { get { var t = BusinessCalendar.Today(DateTimeOffset.UtcNow); return new(t.Year, t.Month, 1); } }
    private static DateOnly M(int offset) => ThisMonth.AddMonths(offset);
    private static string Key(int offset) => M(offset).ToString("yyyy-MM");

    private string _session = null!;
    private WorkspaceDto _workspace = null!;
    private Guid _market, _produce;
    private string Root => $"/api/financial-workspaces/{_workspace.Id}";
    private sealed record IdDto(Guid Id);

    private async Task ArrangeAsync(string user, bool withAccounts = false)
    {
        _session = await SignInAsync(user);
        _workspace = await CreateWorkspaceAsync(_session);
        _market = (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/categories", _session, new { name = "Mercado", type = "Expense" }))).Id;
        _produce = (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/categories", _session, new { name = "Feira", parentCategoryId = _market }))).Id;
        if (!withAccounts) return;
        Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/accounts", _session,
            new { name = "Itaú PF", type = "Checking", financialScope = "PF", openingBalance = "5000.00", openingBalanceDate = M(-1), isPrimary = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/accounts", _session,
            new { name = "Inter PJ", type = "Checking", financialScope = "PJ", openingBalance = "1000.00", openingBalanceDate = M(-1) })).StatusCode);
    }

    private Task<HttpResponseMessage> ChangeAsync(int month, string target, string mode, string action, decimal? amount = null, Guid? category = null) =>
        SendWithCsrfAsync(HttpMethod.Post, $"{Root}/budgets/effective/changes", _session, new { competence = Key(month), target, categoryId = category, mode, action, amount });

    private async Task SetStandardAsync(int from, string target, decimal amount, Guid? category = null) =>
        Assert.Equal(HttpStatusCode.OK, (await ChangeAsync(from, target, "ThisAndNext", "Set", amount, category)).StatusCode);

    private async Task<JsonElement> GetJsonAsync(string url)
    {
        var response = await SendAsync(HttpMethod.Get, url, _session);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private async Task<JsonElement> EffectiveAsync(int month) => await GetJsonAsync($"{Root}/budgets/effective?competence={Key(month)}");
    private async Task<JsonElement> PlanAsync(int month) => await GetJsonAsync($"{Root}/monthly-plan?competence={Key(month)}");

    private static JsonElement Target(JsonElement effective, string target, Guid? category = null) =>
        effective.GetProperty("targets").EnumerateArray().Single(t => t.GetProperty("target").GetString() == target && (category is null || t.GetProperty("categoryId").GetGuid() == category));
    private static string? Amount(JsonElement target) => target.GetProperty("amount").ValueKind == JsonValueKind.Null ? null : target.GetProperty("amount").GetString();
    private static string Origin(JsonElement target) => target.GetProperty("origin").GetString()!;
    private static List<JsonElement> PlanLimits(JsonElement plan) => plan.GetProperty("categoryLimits").EnumerateArray().ToList();
    private static string? PlanLimit(JsonElement plan, Guid category) => PlanLimits(plan).Where(l => l.GetProperty("categoryId").GetGuid() == category).Select(l => l.GetProperty("amount").GetString()).SingleOrDefault();

    private async Task<(int Rules, int Budgets, int Targets, int Aggregates, int History)> CountsAsync()
    {
        await using var db = Db();
        return (await db.FinancialBudgetRuleChanges.CountAsync(), await db.FinancialBudgets.CountAsync(), await db.FinancialBudgetTargets.CountAsync(),
            await db.FinancialMonthlyAggregateTargets.CountAsync(), await db.FinancialPlanHistory.CountAsync());
    }

    // ---- 1. Plano mensal e metas herdadas --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AMonthWithInheritedLimitsAndNoMaterializedBudgetShowsThemInThePlanAndTheyMatchEveryOtherArea()
    {
        await ArrangeAsync("rbp-herdado", withAccounts: true);
        await SetStandardAsync(1, "Category", 800m, _market); await SetStandardAsync(1, "AllCards", 5000m); await SetStandardAsync(1, "TotalExpenses", 12500m);
        var before = await CountsAsync();
        Assert.Equal(0, before.Budgets); // nenhum orçamento mensal materializado

        var plan = await PlanAsync(2);
        Assert.Equal("800.00", PlanLimit(plan, _market));
        Assert.NotEqual(JsonValueKind.Null, PlanLimits(plan).Single().GetProperty("execution").ValueKind); // a execução também vem das metas efetivas

        var effective = await EffectiveAsync(2);
        Assert.Equal("800.00", Amount(Target(effective, "Category", _market))); Assert.Equal("Inherited", Origin(Target(effective, "Category", _market)));
        var overview = await GetJsonAsync($"{Root}/financial-dashboard/overview?competence={Key(2)}&scope=all");
        var overviewCategory = overview.GetProperty("budget").GetProperty("categories").GetProperty("targets").EnumerateArray().Single(t => t.GetProperty("categoryId").GetGuid() == _market);
        Assert.Equal("800.00", overviewCategory.GetProperty("planned").GetString());
        var execution = await GetJsonAsync($"{Root}/budgets/effective/execution?competence={Key(2)}");
        Assert.Contains(execution.GetProperty("targets").EnumerateArray(), t => t.GetProperty("categoryId").ValueKind == JsonValueKind.String && t.GetProperty("categoryId").GetGuid() == _market && t.GetProperty("planned").GetString() == "800.00");
        var projection = await GetJsonAsync($"{Root}/monthly-plan/projection?from={Key(2)}&months=6");
        foreach (var scenario in projection.GetProperty("scenarios").EnumerateArray())
            Assert.Equal(Amount(Target(effective, "AllCards")), scenario.GetProperty("months")[0].GetProperty("budget").GetProperty("cardTarget").GetString());

        Assert.Equal(before, await CountsAsync()); // consultar não materializa orçamento nem grava meta
    }

    [Fact]
    public async Task AMonthlyExceptionReplacesTheInheritedLimitOnlyInThatMonthAndASuspendedOneStaysSuspended()
    {
        await ArrangeAsync("rbp-excecao");
        await SetStandardAsync(1, "Category", 800m, _market);
        Assert.Equal(HttpStatusCode.OK, (await ChangeAsync(2, "Category", "ThisMonth", "Set", 900m, _market)).StatusCode);
        Assert.Equal("900.00", PlanLimit(await PlanAsync(2), _market));
        Assert.Equal("800.00", PlanLimit(await PlanAsync(3), _market));

        Assert.Equal(HttpStatusCode.OK, (await ChangeAsync(3, "Category", "ThisMonth", "Remove", null, _market)).StatusCode);
        var before = await CountsAsync();
        for (var i = 0; i < 2; i++) Assert.Null(PlanLimit(await PlanAsync(3), _market)); // suspensa: continua suspensa a cada leitura
        Assert.Equal("800.00", PlanLimit(await PlanAsync(4), _market)); // os outros meses seguem herdando
        Assert.Equal(before, await CountsAsync());
    }

    [Fact]
    public async Task EditingAnInheritedLimitFromThePlanCreatesAMonthlyExceptionWithoutMaterializingABudget()
    {
        await ArrangeAsync("rbp-editar");
        await SetStandardAsync(1, "Category", 800m, _market);
        var edit = await SendWithCsrfAsync(HttpMethod.Put, $"{Root}/monthly-plan/category-limits", _session, new { competence = Key(2), categoryId = _market, amount = "950.00" });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);

        Assert.Equal("950.00", PlanLimit(await PlanAsync(2), _market)); Assert.Equal("Customized", Origin(Target(await EffectiveAsync(2), "Category", _market)));
        Assert.Equal("800.00", PlanLimit(await PlanAsync(3), _market)); // o padrão e os outros meses não mudaram
        Assert.Equal(0, (await CountsAsync()).Budgets); // nenhum orçamento mensal criado por baixo

        var remove = await SendWithCsrfAsync(HttpMethod.Delete, $"{Root}/monthly-plan/category-limits/{_market}?competence={Key(3)}", _session, null);
        Assert.Equal(HttpStatusCode.OK, remove.StatusCode);
        Assert.Null(PlanLimit(await PlanAsync(3), _market)); Assert.Equal("800.00", PlanLimit(await PlanAsync(4), _market));
        Assert.Equal(0, (await CountsAsync()).Budgets);
    }

    [Fact]
    public async Task ParentAndSubcategoryLimitsStillNeverCoexistEvenAgainstInheritedOnes()
    {
        await ArrangeAsync("rbp-sobreposicao");
        await SetStandardAsync(1, "Category", 800m, _market);
        var before = await CountsAsync();
        var child = await SendWithCsrfAsync(HttpMethod.Put, $"{Root}/monthly-plan/category-limits", _session, new { competence = Key(2), categoryId = _produce, amount = "300.00" });
        Assert.Equal(HttpStatusCode.Conflict, child.StatusCode); // subcategoria de uma categoria com meta herdada
        Assert.Equal(before, await CountsAsync());

        Assert.Equal(HttpStatusCode.BadRequest, (await ChangeAsync(2, "Category", "ThisMonth", "Set", 300m, _produce)).StatusCode); // o orçamento efetivo recusa igualmente
        Assert.Null(PlanLimit(await PlanAsync(2), _produce));
    }

    [Fact]
    public async Task CopyingThePlanDoesNotDuplicateLimitsThatAreAlreadyEffectiveInTheDestination()
    {
        await ArrangeAsync("rbp-copia", withAccounts: true);
        await SetStandardAsync(1, "Category", 800m, _market);
        var before = await CountsAsync();
        var copy = await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/monthly-plan/copy", _session, new { fromCompetence = Key(2), toCompetence = Key(3), confirm = true });
        Assert.Equal(HttpStatusCode.Conflict, copy.StatusCode); // o destino já tem o limite efetivo: nada a copiar
        Assert.Equal(before, await CountsAsync());
    }

    // ---- 2. Restaurar ao padrão com histórico ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task RestoreWorksAfterSeveralEventsInTheMonthKeepsEveryEarlierEventAndAnEditAfterwardsStillWorks()
    {
        await ArrangeAsync("rbp-restaurar");
        await SetStandardAsync(1, "AllCards", 5000m);
        Assert.Equal(HttpStatusCode.OK, (await ChangeAsync(2, "AllCards", "ThisMonth", "Set", 6000m)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ChangeAsync(2, "AllCards", "ThisMonth", "Remove")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ChangeAsync(2, "AllCards", "ThisMonth", "Set", 6500m)).StatusCode);
        List<(int Revision, string Action, decimal? Amount)> Events(IEnumerable<Saldo360.Domain.Budgets.FinancialBudgetRuleChange> rows) =>
            rows.OrderBy(r => r.Revision).Select(r => (r.Revision, r.Action.ToString(), r.Amount)).ToList();
        List<(int, string, decimal?)> snapshot;
        await using (var db = Db()) snapshot = Events(await db.FinancialBudgetRuleChanges.AsNoTracking().Where(c => c.Competence == M(2) && c.Scope == Saldo360.Domain.Budgets.BudgetRuleScope.OnlyCompetence).ToListAsync());

        Assert.Equal(HttpStatusCode.OK, (await ChangeAsync(2, "AllCards", "ThisMonth", "Restore")).StatusCode);

        var card = Target(await EffectiveAsync(2), "AllCards");
        Assert.Equal("5000.00", Amount(card)); Assert.Equal("Inherited", Origin(card));
        await using (var db = Db())
        {
            var after = Events(await db.FinancialBudgetRuleChanges.AsNoTracking().Where(c => c.Competence == M(2) && c.Scope == Saldo360.Domain.Budgets.BudgetRuleScope.OnlyCompetence).ToListAsync());
            Assert.Equal(snapshot.Count + 1, after.Count);
            Assert.Equal(snapshot, after.Take(snapshot.Count).ToList()); // os eventos anteriores continuam intactos
            Assert.Equal("Restore", after[^1].Action);
        }

        Assert.Equal(HttpStatusCode.OK, (await ChangeAsync(2, "AllCards", "ThisMonth", "Set", 5400m)).StatusCode); // edição posterior
        Assert.Equal("5400.00", Amount(Target(await EffectiveAsync(2), "AllCards"))); Assert.Equal("Customized", Origin(Target(await EffectiveAsync(2), "AllCards")));
    }

    [Fact]
    public async Task RestoringTwiceCreatesNoUndueChangeAndConcurrentRestoresWriteAtMostOneEvent()
    {
        await ArrangeAsync("rbp-restaurar-duas");
        await SetStandardAsync(1, "AllCards", 5000m);
        Assert.Equal(HttpStatusCode.OK, (await ChangeAsync(2, "AllCards", "ThisMonth", "Set", 6000m)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ChangeAsync(2, "AllCards", "ThisMonth", "Restore")).StatusCode);
        var afterFirst = await CountsAsync();
        Assert.Equal(HttpStatusCode.OK, (await ChangeAsync(2, "AllCards", "ThisMonth", "Restore")).StatusCode);
        Assert.Equal(afterFirst, await CountsAsync()); // a segunda restauração não grava

        Assert.Equal(HttpStatusCode.OK, (await ChangeAsync(3, "AllCards", "ThisMonth", "Set", 7000m)).StatusCode);
        var beforeRace = (await CountsAsync()).Rules;
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => ChangeAsync(3, "AllCards", "ThisMonth", "Restore")));
        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, r.StatusCode.ToString()));
        Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Equal(beforeRace + 1, (await CountsAsync()).Rules); // um único evento de restauração
        Assert.Equal("5000.00", Amount(Target(await EffectiveAsync(3), "AllCards")));
        await using var db = Db();
        var rows = await db.FinancialBudgetRuleChanges.AsNoTracking().Where(c => c.Competence == M(3)).ToListAsync();
        Assert.Equal(rows.Count, rows.Select(r => r.Revision).Distinct().Count());
    }

    [Fact]
    public async Task RestoreOverAHistoricalMonthlyRecordFollowsTheStandardInForceInACurrentOrFutureMonthAndLeavesTheRecordIntact()
    {
        await ArrangeAsync("rbp-restaurar-historico");
        var budget = (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/budgets", _session, new { competence = Key(1) }))).Id;
        Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/budgets/{budget}/targets", _session, new { groupingType = "AllCards", plannedAmount = "4200.00" })).StatusCode);
        await SetStandardAsync(1, "AllCards", 5000m);
        Assert.Equal("Historical", Origin(Target(await EffectiveAsync(1), "AllCards")));

        Assert.Equal(HttpStatusCode.OK, (await ChangeAsync(1, "AllCards", "ThisMonth", "Restore")).StatusCode);
        var card = Target(await EffectiveAsync(1), "AllCards");
        Assert.Equal("5000.00", Amount(card)); Assert.Equal("Inherited", Origin(card));
        await using (var db = Db()) Assert.Equal(4200m, (await db.FinancialBudgetTargets.AsNoTracking().SingleAsync(t => t.BudgetId == budget)).PlannedAmount); // registro histórico intacto

        var again = await CountsAsync();
        Assert.Equal(HttpStatusCode.OK, (await ChangeAsync(1, "AllCards", "ThisMonth", "Restore")).StatusCode);
        Assert.Equal(again, await CountsAsync());
        Assert.Equal(HttpStatusCode.OK, (await ChangeAsync(1, "AllCards", "ThisMonth", "Set", 4800m)).StatusCode);
        Assert.Equal("4800.00", Amount(Target(await EffectiveAsync(1), "AllCards")));
    }

    [Fact]
    public async Task ClosedMonthsKeepTheirHistoricalRecordAndRestoreWithoutAStandardIsRefused()
    {
        await ArrangeAsync("rbp-encerrada");
        var closed = (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/budgets", _session, new { competence = Key(-1) }))).Id;
        Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/budgets/{closed}/targets", _session, new { groupingType = "AllCards", plannedAmount = "4200.00" })).StatusCode);
        var open = (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/budgets", _session, new { competence = Key(2) }))).Id;
        Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/budgets/{open}/targets", _session, new { groupingType = "AllCards", plannedAmount = "4300.00" })).StatusCode);
        await SetStandardAsync(-3, "AllCards", 5000m);
        var before = await CountsAsync();

        var closedRestore = await ChangeAsync(-1, "AllCards", "ThisMonth", "Restore");
        Assert.Equal(HttpStatusCode.Conflict, closedRestore.StatusCode); Assert.Contains("Competência encerrada", await closedRestore.Content.ReadAsStringAsync());
        Assert.Equal("4200.00", Amount(Target(await EffectiveAsync(-1), "AllCards"))); Assert.Equal("Historical", Origin(Target(await EffectiveAsync(-1), "AllCards")));
        Assert.Equal(before, await CountsAsync());

        // Sem padrão em vigor para a meta (aqui, o total), restaurar não esconde o registro histórico.
        var noStandard = (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/budgets/{open}/targets", _session, new { groupingType = "FixedExpenses", plannedAmount = "3000.00" })));
        Assert.NotEqual(Guid.Empty, noStandard.Id);
        var refused = await ChangeAsync(2, "FixedExpenses", "ThisMonth", "Restore");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode); Assert.Contains("Não há padrão vigente", await refused.Content.ReadAsStringAsync());
        Assert.Equal("3000.00", Amount(Target(await EffectiveAsync(2), "FixedExpenses")));
    }

    [Fact]
    public async Task AClosedMonthWithoutAHistoricalRecordCanStillUndoItsOwnException()
    {
        await ArrangeAsync("rbp-encerrada-excecao");
        await SetStandardAsync(-3, "AllCards", 5000m);
        Assert.Equal(HttpStatusCode.OK, (await ChangeAsync(-1, "AllCards", "ThisMonth", "Set", 5500m)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ChangeAsync(-1, "AllCards", "ThisMonth", "Restore")).StatusCode); // evento novo; o anterior permanece
        Assert.Equal("5000.00", Amount(Target(await EffectiveAsync(-1), "AllCards")));
        await using var db = Db();
        Assert.Equal(2, await db.FinancialBudgetRuleChanges.CountAsync(c => c.Competence == M(-1)));
    }
}
