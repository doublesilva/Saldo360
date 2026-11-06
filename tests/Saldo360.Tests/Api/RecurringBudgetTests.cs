using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.Common;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>
/// T12.1: orçamento mensal recorrente pela API real (Postgres). Meses relativos ao mês corrente: M(-1) = registro mensal histórico; o padrão começa em M(1). O mesmo mês e workspace
/// precisam mostrar os mesmos limites no orçamento, na Visão Geral, na execução e na projeção (Atual e Tetos).
/// </summary>
[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Budgets")]
[Trait("Dependency", "PostgreSQL")]
public sealed class RecurringBudgetTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private static DateOnly ThisMonth { get { var t = BusinessCalendar.Today(DateTimeOffset.UtcNow); return new(t.Year, t.Month, 1); } }
    private static DateOnly M(int offset) => ThisMonth.AddMonths(offset);
    private static string Key(int offset) => M(offset).ToString("yyyy-MM");

    private string _session = null!;
    private WorkspaceDto _workspace = null!;
    private Guid _expense, _account;
    private string Root => $"/api/financial-workspaces/{_workspace.Id}";
    private sealed record IdDto(Guid Id);

    private async Task ArrangeAsync(string user, bool withAccounts = false)
    {
        _session = await SignInAsync(user);
        _workspace = await CreateWorkspaceAsync(_session);
        _expense = (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/categories", _session, new { name = "Mercado", type = "Expense" }))).Id;
        if (!withAccounts) return;
        _account = (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/accounts", _session,
            new { name = "Itaú PF", type = "Checking", financialScope = "PF", openingBalance = "5000.00", openingBalanceDate = M(-1), isPrimary = true }))).Id;
        Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/accounts", _session,
            new { name = "Inter PJ", type = "Checking", financialScope = "PJ", openingBalance = "1000.00", openingBalanceDate = M(-1) })).StatusCode);
    }

    private Task<HttpResponseMessage> ChangeAsync(int month, string target, string mode, string action, decimal? amount = null, Guid? category = null, string? session = null) =>
        SendWithCsrfAsync(HttpMethod.Post, $"{Root}/budgets/effective/changes", session ?? _session, new { competence = Key(month), target, categoryId = category, mode, action, amount });

    private async Task SetStandardAsync(int from, string target, decimal amount, Guid? category = null) =>
        Assert.Equal(HttpStatusCode.OK, (await ChangeAsync(from, target, "ThisAndNext", "Set", amount, category)).StatusCode);

    private async Task<JsonElement> EffectiveAsync(int month, string? session = null)
    {
        var response = await SendAsync(HttpMethod.Get, $"{Root}/budgets/effective?competence={Key(month)}", session ?? _session);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static JsonElement Target(JsonElement effective, string target, Guid? category = null) =>
        effective.GetProperty("targets").EnumerateArray().Single(t => t.GetProperty("target").GetString() == target
            && (category is null || t.GetProperty("categoryId").GetGuid() == category));

    private static string? Amount(JsonElement target) => target.GetProperty("amount").ValueKind == JsonValueKind.Null ? null : target.GetProperty("amount").GetString();
    private static string Origin(JsonElement target) => target.GetProperty("origin").GetString()!;

    private async Task<(int Rules, int Budgets, int Targets, int Aggregates)> CountsAsync()
    {
        await using var db = Db();
        return (await db.FinancialBudgetRuleChanges.CountAsync(), await db.FinancialBudgets.CountAsync(), await db.FinancialBudgetTargets.CountAsync(), await db.FinancialMonthlyAggregateTargets.CountAsync());
    }

    private async Task<Guid> HistoricalBudgetAsync(int month, string cards, string? fixedExpenses = null)
    {
        var budget = (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/budgets", _session, new { competence = Key(month) }))).Id;
        Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/budgets/{budget}/targets", _session, new { groupingType = "AllCards", plannedAmount = cards })).StatusCode);
        if (fixedExpenses is not null)
            Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/budgets/{budget}/targets", _session, new { groupingType = "FixedExpenses", plannedAmount = fixedExpenses })).StatusCode);
        return budget;
    }

    // ---- Herança, histórico, exceções --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AMonthWithoutItsOwnConfigurationInheritsTheStandardAndShowsWhereTheValueComesFrom()
    {
        await ArrangeAsync("rb-heranca");
        Assert.Equal("Undefined", Origin(Target(await EffectiveAsync(1), "AllCards"))); // sem configuração: não definida (nunca zero)
        await SetStandardAsync(1, "AllCards", 5000m); await SetStandardAsync(1, "TotalExpenses", 12500m);

        foreach (var offset in new[] { 1, 2, 8 })
        {
            var effective = await EffectiveAsync(offset);
            var cards = Target(effective, "AllCards");
            Assert.Equal("5000.00", Amount(cards)); Assert.Equal("Inherited", Origin(cards)); Assert.Equal(Key(1), cards.GetProperty("standardFrom").GetString());
            Assert.Equal("12500.00", Amount(Target(effective, "TotalExpenses"))); Assert.Equal("7500.00", effective.GetProperty("outsideCardsTarget").GetString());
            Assert.True(effective.GetProperty("standardConfigured").GetBoolean());
        }
    }

    [Fact]
    public async Task TheHistoricalMonthStaysUntouchedAndTheMonthBeforeTheStandardIsNotFilled()
    {
        await ArrangeAsync("rb-historico");
        await HistoricalBudgetAsync(-1, "4200.00", fixedExpenses: "3000.00");
        await SetStandardAsync(1, "AllCards", 5000m);

        var historical = await EffectiveAsync(-1);
        Assert.Equal("4200.00", Amount(Target(historical, "AllCards"))); Assert.Equal("Historical", Origin(Target(historical, "AllCards")));
        Assert.Equal("3000.00", Amount(Target(historical, "FixedExpenses")));
        Assert.Equal("Undefined", Origin(Target(await EffectiveAsync(0), "AllCards"))); // o mês corrente fica antes da vigência
        Assert.Equal("5000.00", Amount(Target(await EffectiveAsync(1), "AllCards")));
    }

    [Fact]
    public async Task AnExceptionForOneMonthDoesNotChangeTheNextAndRestoringReturnsToTheStandard()
    {
        await ArrangeAsync("rb-excecao");
        await SetStandardAsync(1, "AllCards", 5000m);
        Assert.Equal(HttpStatusCode.OK, (await ChangeAsync(1, "AllCards", "ThisMonth", "Set", 6500m)).StatusCode);

        var first = Target(await EffectiveAsync(1), "AllCards");
        Assert.Equal("6500.00", Amount(first)); Assert.Equal("Customized", Origin(first));
        var second = Target(await EffectiveAsync(2), "AllCards");
        Assert.Equal("5000.00", Amount(second)); Assert.Equal("Inherited", Origin(second));

        Assert.Equal(HttpStatusCode.OK, (await ChangeAsync(1, "AllCards", "ThisMonth", "Restore")).StatusCode);
        var restored = Target(await EffectiveAsync(1), "AllCards");
        Assert.Equal("5000.00", Amount(restored)); Assert.Equal("Inherited", Origin(restored));
        var before = await CountsAsync();
        Assert.Equal(HttpStatusCode.OK, (await ChangeAsync(1, "AllCards", "ThisMonth", "Restore")).StatusCode); // restaurar de novo é idempotente
        Assert.Equal(before, await CountsAsync()); // e não grava nenhum evento
    }

    [Fact]
    public async Task ChangingTheStandardFromALaterMonthOnlyAffectsFromThere()
    {
        await ArrangeAsync("rb-vigencia");
        await SetStandardAsync(1, "AllCards", 5000m);
        await SetStandardAsync(4, "AllCards", 5600m);

        foreach (var offset in new[] { 1, 2, 3 }) Assert.Equal("5000.00", Amount(Target(await EffectiveAsync(offset), "AllCards")));
        Assert.Equal("5600.00", Amount(Target(await EffectiveAsync(4), "AllCards"))); Assert.Equal("5600.00", Amount(Target(await EffectiveAsync(9), "AllCards")));
        Assert.Equal(Key(4), Target(await EffectiveAsync(5), "AllCards").GetProperty("standardFrom").GetString());
    }

    [Fact]
    public async Task ThisAndNextOverAnExistingExceptionOfThatMonthMakesTheMonthFollowTheNewStandard()
    {
        await ArrangeAsync("rb-este-e-proximos");
        await SetStandardAsync(1, "AllCards", 5000m);
        Assert.Equal(HttpStatusCode.OK, (await ChangeAsync(2, "AllCards", "ThisMonth", "Set", 9000m)).StatusCode);
        await SetStandardAsync(2, "AllCards", 5300m);

        var month = Target(await EffectiveAsync(2), "AllCards");
        Assert.Equal("5300.00", Amount(month)); Assert.Equal("Inherited", Origin(month));
        Assert.Equal("5000.00", Amount(Target(await EffectiveAsync(1), "AllCards")));
    }

    [Fact]
    public async Task ATargetSuspendedInAMonthDoesNotComeBackThereButInheritsAgainNextMonth()
    {
        await ArrangeAsync("rb-suspensa");
        await SetStandardAsync(1, "AllCards", 5000m);
        Assert.Equal(HttpStatusCode.OK, (await ChangeAsync(2, "AllCards", "ThisMonth", "Remove")).StatusCode);

        var suspended = Target(await EffectiveAsync(2), "AllCards");
        Assert.Null(Amount(suspended)); Assert.True(suspended.GetProperty("removed").GetBoolean());
        Assert.Equal("5000.00", Amount(Target(await EffectiveAsync(3), "AllCards")));

        Assert.Equal(HttpStatusCode.OK, (await ChangeAsync(5, "AllCards", "ThisAndNext", "Remove")).StatusCode); // retirar do padrão daí em diante
        Assert.Equal("5000.00", Amount(Target(await EffectiveAsync(4), "AllCards")));
        Assert.Null(Amount(Target(await EffectiveAsync(7), "AllCards")));
        Assert.Equal(HttpStatusCode.Conflict, (await ChangeAsync(7, "AllCards", "ThisMonth", "Remove")).StatusCode); // nada a retirar
    }

    // ---- Validação ---------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ATotalSmallerThanTheCardsTargetAndOverlappingCategoriesAreRejected()
    {
        await ArrangeAsync("rb-validacao");
        await SetStandardAsync(1, "AllCards", 5000m);
        var tooSmall = await ChangeAsync(1, "TotalExpenses", "ThisAndNext", "Set", 4000m);
        Assert.Equal(HttpStatusCode.BadRequest, tooSmall.StatusCode);
        Assert.Contains("não pode ser menor", await tooSmall.Content.ReadAsStringAsync());
        Assert.Equal("Undefined", Origin(Target(await EffectiveAsync(1), "TotalExpenses"))); // nada foi gravado

        var child = (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/categories", _session, new { name = "Feira", parentCategoryId = _expense }))).Id;
        await SetStandardAsync(1, "Category", 800m, _expense);
        var overlapping = await ChangeAsync(1, "Category", "ThisAndNext", "Set", 300m, child);
        Assert.Equal(HttpStatusCode.BadRequest, overlapping.StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await ChangeAsync(1, "AllCards", "ThisAndNext", "Set", -1m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ChangeAsync(1, "AllCards", "ThisAndNext", "Set", 10.123m)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ChangeAsync(1, "AllCards", "ThisAndNext", "Restore")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ChangeAsync(1, "AllCards", "Whenever", "Set", 1m)).StatusCode);
    }

    [Fact]
    public async Task AnExceptionThatBreaksTheTotalRuleIsRejectedAgainstTheInheritedTotal()
    {
        await ArrangeAsync("rb-excecao-invalida");
        await SetStandardAsync(1, "TotalExpenses", 12500m);
        await SetStandardAsync(1, "AllCards", 5000m);
        var response = await ChangeAsync(2, "AllCards", "ThisMonth", "Set", 13000m); // cartões acima do total herdado
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("5000.00", Amount(Target(await EffectiveAsync(2), "AllCards")));
    }

    // ---- Somente leitura, concorrência, permissões e histórico ------------------------------------------------------------------------------------------

    [Fact]
    public async Task RepeatedReadsNeverWriteAnythingNorMaterializeMonthlyBudgets()
    {
        await ArrangeAsync("rb-leitura", withAccounts: true);
        await SetStandardAsync(1, "AllCards", 5000m); await SetStandardAsync(1, "TotalExpenses", 12500m);
        var before = await CountsAsync();
        for (var i = 0; i < 2; i++)
        {
            await EffectiveAsync(1); await EffectiveAsync(7);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"{Root}/budgets/effective/execution?competence={Key(2)}", _session)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"{Root}/financial-dashboard/overview?competence={Key(1)}&scope=all", _session)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"{Root}/monthly-plan/projection?from={Key(1)}&months=6", _session)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"{Root}/budgets/effective/history", _session)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"{Root}/budgets/effective/reference?source={Key(1)}&from={Key(2)}", _session)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Get, $"{Root}/budgets?competence={Key(2)}", _session)).StatusCode);
        }
        Assert.Equal(before, await CountsAsync());
    }

    [Fact]
    public async Task ConcurrentChangesToTheSameMonthNeverCreateDuplicateRevisions()
    {
        await ArrangeAsync("rb-concorrencia");
        await SetStandardAsync(1, "AllCards", 5000m);
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => ChangeAsync(2, "AllCards", "ThisMonth", "Set", 6000m + i)));

        Assert.All(responses, r => Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, r.StatusCode.ToString()));
        var succeeded = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        Assert.True(succeeded >= 1);
        await using var db = Db();
        var rows = await db.FinancialBudgetRuleChanges.AsNoTracking().Where(c => c.Scope == Saldo360.Domain.Budgets.BudgetRuleScope.OnlyCompetence && c.Competence == M(2)).ToListAsync();
        Assert.Equal(succeeded, rows.Count);
        Assert.Equal(rows.Count, rows.Select(r => r.Revision).Distinct().Count());
        var winner = rows.OrderByDescending(r => r.Revision).First();
        Assert.Equal(winner.Amount!.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), Amount(Target(await EffectiveAsync(2), "AllCards")));
    }

    [Fact]
    public async Task OnlyMembersCanReadOrChangeAndTheHistoryKeepsEveryChange()
    {
        await ArrangeAsync("rb-permissao");
        var outsider = await SignInAsync("rb-permissao-fora");
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"{Root}/budgets/effective?competence={Key(1)}", outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ChangeAsync(1, "AllCards", "ThisAndNext", "Set", 5000m, session: outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"{Root}/budgets/effective/history", outsider)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync($"{Root}/budgets/effective?competence={Key(1)}")).StatusCode);

        await SetStandardAsync(1, "AllCards", 5000m); await SetStandardAsync(1, "AllCards", 5200m);
        Assert.Equal(HttpStatusCode.OK, (await ChangeAsync(2, "AllCards", "ThisMonth", "Set", 6000m)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ChangeAsync(2, "AllCards", "ThisMonth", "Restore")).StatusCode);
        var history = JsonDocument.Parse(await (await SendAsync(HttpMethod.Get, $"{Root}/budgets/effective/history", _session)).Content.ReadAsStringAsync()).RootElement;
        var rows = history.EnumerateArray().ToList();
        Assert.Equal(4, rows.Count); // nada foi apagado nem sobrescrito: duas versões do padrão, uma exceção e a sua restauração
        Assert.Equal(2, rows.Count(r => r.GetProperty("scope").GetString() == "FromCompetence" && r.GetProperty("competence").GetString() == Key(1)));
        Assert.Contains(rows, r => r.GetProperty("revision").GetInt32() == 2 && r.GetProperty("amount").GetString() == "5200.00");
    }

    // ---- Consistência entre Orçamento, execução, Visão Geral e Projeção (Atual e Tetos) ------------------------------------------------------------------

    [Fact]
    public async Task BudgetExecutionOverviewAndTheProjectionShowTheSameRecurringLimitsInBothScenarios()
    {
        await ArrangeAsync("rb-consistencia", withAccounts: true);
        await SetStandardAsync(1, "AllCards", 5000m); await SetStandardAsync(1, "TotalExpenses", 12500m); await SetStandardAsync(1, "FixedExpenses", 3000m);
        Assert.Equal(HttpStatusCode.OK, (await ChangeAsync(3, "AllCards", "ThisMonth", "Set", 5400m)).StatusCode);

        foreach (var months in new[] { 6, 13 })
        {
            var projection = JsonDocument.Parse(await (await SendAsync(HttpMethod.Get, $"{Root}/monthly-plan/projection?from={Key(1)}&months={months}", _session)).Content.ReadAsStringAsync()).RootElement;
            foreach (var scenario in projection.GetProperty("scenarios").EnumerateArray())
            {
                var list = scenario.GetProperty("months").EnumerateArray().ToList();
                Assert.Equal(months, list.Count);
                for (var i = 0; i < list.Count; i++)
                {
                    var effective = await EffectiveAsync(1 + i);
                    var card = Amount(Target(effective, "AllCards")); var outside = effective.GetProperty("outsideCardsTarget").GetString();
                    Assert.Equal(card, list[i].GetProperty("budget").GetProperty("cardTarget").GetString());
                    Assert.Equal(outside, list[i].GetProperty("budget").GetProperty("outsideTarget").GetString());
                }
            }
            Assert.Contains(projection.GetProperty("scenarios").EnumerateArray(), s => s.GetProperty("name").GetString() == "Ceilings");
        }

        var overview = JsonDocument.Parse(await (await SendAsync(HttpMethod.Get, $"{Root}/financial-dashboard/overview?competence={Key(3)}&scope=all", _session)).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("5400.00", overview.GetProperty("budgetControl").GetProperty("cards").GetProperty("target").GetString());
        Assert.Equal("5400.00", overview.GetProperty("budget").GetProperty("cards").GetProperty("planned").GetString());
        Assert.Equal("3000.00", overview.GetProperty("budget").GetProperty("fixed").GetProperty("planned").GetString());
        Assert.Equal("12500.00", overview.GetProperty("budgetControl").GetProperty("total").GetProperty("target").GetString());
        Assert.Equal("7100.00", overview.GetProperty("budgetControl").GetProperty("outsideCards").GetProperty("target").GetString());
        // T12.2: a Visão Geral diz de onde vem a meta total (herdada do padrão que começou no mês 1), pelo mesmo resolvedor.
        Assert.Equal("Inherited", overview.GetProperty("budgetControl").GetProperty("totalTargetOrigin").GetString());
        Assert.Equal(Key(1), overview.GetProperty("budgetControl").GetProperty("totalTargetFrom").GetString());

        var execution = JsonDocument.Parse(await (await SendAsync(HttpMethod.Get, $"{Root}/budgets/effective/execution?competence={Key(3)}", _session)).Content.ReadAsStringAsync()).RootElement;
        var planned = execution.GetProperty("targets").EnumerateArray().ToDictionary(t => t.GetProperty("groupingType").GetString()!, t => t.GetProperty("planned").GetString());
        Assert.Equal("5400.00", planned["AllCards"]); Assert.Equal("3000.00", planned["FixedExpenses"]);
    }

    [Fact]
    public async Task AnInheritedTargetHasItsOwnCompositionAndAFixedExpenseOnTheCardCountsOnceInEachPerspectiveButOnceInTheTotal()
    {
        await ArrangeAsync("rb-composicao", withAccounts: true);
        await SetStandardAsync(0, "AllCards", 5000m); await SetStandardAsync(0, "FixedExpenses", 3000m); await SetStandardAsync(0, "TotalExpenses", 12500m);
        var card = (await ReadAsync<IdDto>(await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/credit-cards", _session,
            new { name = "Nubank", issuer = "Banco", creditLimit = "10000.00", closingDay = 5, dueDay = 10, defaultPaymentAccountId = _account }))).Id;
        Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/credit-cards/{card}/purchases", _session, new
        {
            description = "Aluguel no cartão", purchaseDate = M(0), totalAmount = "1000.00", installmentCount = 1, categoryId = _expense, amountMode = "Total",
            previouslyPaidInstallments = 0, firstPendingCompetence = M(0), expenseNature = "Fixed",
        })).StatusCode);

        var execution = JsonDocument.Parse(await (await SendAsync(HttpMethod.Get, $"{Root}/budgets/effective/execution?competence={Key(0)}", _session)).Content.ReadAsStringAsync()).RootElement;
        var targets = execution.GetProperty("targets").EnumerateArray().ToDictionary(t => t.GetProperty("groupingType").GetString()!);
        Assert.Equal("1000.00", targets["AllCards"].GetProperty("consumed").GetString());
        Assert.Equal("1000.00", targets["FixedExpenses"].GetProperty("consumed").GetString()); // classificada como fixa, no cartão

        var overview = JsonDocument.Parse(await (await SendAsync(HttpMethod.Get, $"{Root}/financial-dashboard/overview?competence={Key(0)}&scope=all", _session)).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("1000.00", overview.GetProperty("budgetControl").GetProperty("total").GetProperty("projected").GetString()); // o orçamento total conta o gasto uma só vez

        var cardTargetId = targets["AllCards"].GetProperty("targetId").GetGuid();
        var composition = await SendAsync(HttpMethod.Get, $"{Root}/budgets/effective/targets/{cardTargetId}/composition?competence={Key(0)}", _session);
        Assert.Equal(HttpStatusCode.OK, composition.StatusCode);
        Assert.Equal(1, JsonDocument.Parse(await composition.Content.ReadAsStringAsync()).RootElement.GetProperty("totalCount").GetInt32());
    }

    // ---- Usar o orçamento existente como referência ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task UsingAnExistingMonthAsReferenceShowsAPreviewWithWarningsAndOnlyAppliesWhatWasChosen()
    {
        await ArrangeAsync("rb-referencia");
        await HistoricalBudgetAsync(-1, "4200.00", fixedExpenses: "3000.00");
        var budget = (await EffectiveAsync(-1)).GetProperty("budgetId").GetGuid();
        Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/budgets/{budget}/targets", _session, new { name = "Mercado", groupingType = "Category", categoryId = _expense, plannedAmount = "900.00" })).StatusCode);

        var preview = JsonDocument.Parse(await (await SendAsync(HttpMethod.Get, $"{Root}/budgets/effective/reference?source={Key(-1)}&from={Key(1)}", _session)).Content.ReadAsStringAsync()).RootElement;
        var warnings = preview.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).ToList();
        Assert.Contains("FixedExpensesNotOutsideCards", warnings); Assert.Contains("CategoriesMayBeInsideCards", warnings); Assert.Contains("TotalExpensesRequiredForCeilings", warnings);
        Assert.Equal(3, preview.GetProperty("items").GetArrayLength());
        Assert.Equal("Undefined", Origin(Target(await EffectiveAsync(1), "AllCards"))); // a prévia não configura nada

        var applied = await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/budgets/effective/reference", _session,
            new { source = Key(-1), from = Key(1), targets = new[] { new { target = "AllCards", categoryId = (Guid?)null } } });
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.Equal("4200.00", Amount(Target(await EffectiveAsync(2), "AllCards"))); Assert.Equal("Inherited", Origin(Target(await EffectiveAsync(2), "AllCards")));
        Assert.Equal("Undefined", Origin(Target(await EffectiveAsync(2), "FixedExpenses"))); // só o que foi escolhido virou padrão
        Assert.Equal("Historical", Origin(Target(await EffectiveAsync(-1), "AllCards"))); // o mês de referência não mudou
        Assert.Equal(HttpStatusCode.BadRequest, (await SendWithCsrfAsync(HttpMethod.Post, $"{Root}/budgets/effective/reference", _session, new { source = Key(-1), from = Key(1), targets = Array.Empty<object>() })).StatusCode);
    }
}
