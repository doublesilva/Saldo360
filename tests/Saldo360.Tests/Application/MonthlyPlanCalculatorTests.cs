using Saldo360.Application.Dashboard.PlanActual;
using Saldo360.Application.MonthlyPlan;
using Saldo360.Domain.Planning;

namespace Saldo360.Tests.Application;

/// <summary>T10.2: fórmulas do planejamento mensal. Regras puras, sem banco, rede ou relógio.</summary>
[Trait("Category", "CORE")]
[Trait("Area", "Planning")]
public sealed class MonthlyPlanCalculatorTests
{
    private static readonly DateOnly Oct = new(2026, 10, 1);
    private static readonly Guid Pf = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid Pj = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
    private static readonly Guid Food = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private static readonly Guid Housing = Guid.Parse("00000000-0000-0000-0000-0000000000c2");

    private static PlanLineFact Line(PlanLineKind kind, decimal amount, Guid? account = null, Guid? category = null, PlanLineNature? nature = PlanLineNature.Fixed, Guid? destination = null) =>
        new(Guid.NewGuid(), Oct, kind, kind == PlanLineKind.Transfer ? null : nature, "linha", account, destination, category, amount, amount, null, null, null, null, DateTimeOffset.UnixEpoch);

    private static PlanLimitFact Limit(Guid category, decimal amount) => new(Guid.NewGuid(), Guid.NewGuid(), Oct, category, "categoria", amount, null);

    // ---- Planejado por escopo ------------------------------------------------------------------------------------------------------

    [Fact]
    public void WithoutAnyPlanEverythingIsNotDefinedAndNeverAFictionalZero()
    {
        var totals = MonthlyPlanCalculator.Planned(PlanFacts.Empty, null);

        Assert.Equal((null, null, false, 0m), (totals.Income, totals.Expense, totals.HasPlan, totals.Reserve));
        Assert.Null(MonthlyPlanCalculator.Result(totals.Income, totals.Expense));
    }

    [Fact]
    public void IncomeAndExpenseAreSummedByLinesAndTheAccountScopeKeepsOnlyItsOwnLines()
    {
        var facts = new PlanFacts([
            Line(PlanLineKind.Income, 8000.10m, Pj), Line(PlanLineKind.Income, 500m, Pf), Line(PlanLineKind.Income, 100m),
            Line(PlanLineKind.Expense, 2000m, Pf, Housing), Line(PlanLineKind.Expense, 300.50m, Pj)], []);

        var all = MonthlyPlanCalculator.Planned(facts, null);
        var pf = MonthlyPlanCalculator.Planned(facts, Pf);
        var pj = MonthlyPlanCalculator.Planned(facts, Pj);

        // Todas as contas inclui a linha sem conta; a conta específica nunca recebe a linha consolidada.
        Assert.Equal((8600.10m, 2300.50m), (all.Income, all.Expense));
        Assert.Equal((500m, 2000m), (pf.Income, pf.Expense));
        Assert.Equal((8000.10m, 300.50m), (pj.Income, pj.Expense));
        Assert.Equal(6299.60m, MonthlyPlanCalculator.Result(all.Income, all.Expense));
    }

    [Fact]
    public void TheCategoryLimitReplacesTheItemizedLinesOfThatCategoryAndNeverAddsToThem()
    {
        // Moradia: limite de 2.500 detalhado por aluguel de 2.000 e condomínio de 400 (soma 2.400, dentro do limite). Alimentação: limite sem linhas.
        // Contabilidade: linha sem limite soma pela própria linha. O total NÃO é 2.500 + 2.400 + 2.500 + 300.
        var facts = new PlanFacts([
            Line(PlanLineKind.Expense, 2000m, Pf, Housing), Line(PlanLineKind.Expense, 400m, Pf, Housing), Line(PlanLineKind.Expense, 300m, Pj)],
            [Limit(Housing, 2500m), Limit(Food, 2500m)]);

        var totals = MonthlyPlanCalculator.Planned(facts, null);

        Assert.Equal(5300m, totals.Expense);
        Assert.Equal(2400m, MonthlyPlanCalculator.Itemized(facts, Housing));
    }

    [Fact]
    public void ItemizedLinesAboveTheLimitDoNotRaiseThePlannedTotalButAreDetectable()
    {
        var facts = new PlanFacts([Line(PlanLineKind.Expense, 2000m, Pf, Housing), Line(PlanLineKind.Expense, 900m, Pf, Housing)], [Limit(Housing, 2500m)]);

        Assert.Equal(2500m, MonthlyPlanCalculator.Planned(facts, null).Expense);
        Assert.True(MonthlyPlanCalculator.Itemized(facts, Housing) > 2500m);
    }

    [Fact]
    public void InASpecificAccountTheConsolidatedCategoryLimitIsNotAttributableAndNeverApplied()
    {
        var facts = new PlanFacts([Line(PlanLineKind.Expense, 700m, Pf)], [Limit(Food, 2500m)]);

        var pf = MonthlyPlanCalculator.Planned(facts, Pf);
        var pj = MonthlyPlanCalculator.Planned(facts, Pj);

        Assert.Equal((700m, true), (pf.Expense, pf.LimitsNotAttributable));
        Assert.Equal((null, true), (pj.Expense, pj.LimitsNotAttributable));
    }

    [Fact]
    public void ReservesAndProvisionsAreOutsideTheConsumptionBaseAndTransfersAreNeitherIncomeNorExpense()
    {
        var facts = new PlanFacts([
            Line(PlanLineKind.Expense, 1000m, Pf), Line(PlanLineKind.Expense, 600m, Pf, nature: PlanLineNature.Reserve),
            Line(PlanLineKind.Transfer, 4000m, Pj, destination: Pf)], []);

        var all = MonthlyPlanCalculator.Planned(facts, null);
        var pf = MonthlyPlanCalculator.Planned(facts, Pf);
        var pj = MonthlyPlanCalculator.Planned(facts, Pj);

        Assert.Equal((1000m, 600m, 1, 1), (all.Expense, all.Reserve, all.ReserveLines, all.TransferCount));
        // A transferência não cria receita nem despesa consolidada; cada conta vê só a sua perna do caixa.
        Assert.Null(all.Income);
        Assert.Equal((4000m, 4000m), (all.TransfersOut, all.TransfersIn));
        Assert.Equal((0m, 4000m), (pf.TransfersOut, pf.TransfersIn));
        Assert.Equal((4000m, 0m), (pj.TransfersOut, pj.TransfersIn));
        Assert.Null(pj.Income);
    }

    [Fact]
    public void ThePlanReplacesTheLegacyPlannedBaseOnlyWhenThereAreLinesAndTheLegacyTargetsNeverSumWithIt()
    {
        var legacy = new PlannedFacts(900m, 2, 1200m, 3);
        var withLines = MonthlyPlanCalculator.Planned(new PlanFacts([Line(PlanLineKind.Income, 7000m, Pj), Line(PlanLineKind.Expense, 1500m, Pj)], []), null);
        var withoutLines = MonthlyPlanCalculator.Planned(PlanFacts.Empty, null);
        var limitsOnly = MonthlyPlanCalculator.Planned(new PlanFacts([], [Limit(Food, 2500m)]), null);

        Assert.Equal((7000m, "MonthlyPlan"), MonthlyPlanCalculator.PlannedIncome(withLines, legacy));
        Assert.Equal((900m, "PlannedTransactions"), MonthlyPlanCalculator.PlannedIncome(withoutLines, legacy));
        Assert.Equal((null, null), MonthlyPlanCalculator.PlannedIncome(withoutLines, new PlannedFacts(0m, 0, 0m, 0)));

        Assert.Equal((1500m, "MonthlyPlan"), MonthlyPlanCalculator.PlannedConsumption(withLines, 5000m, 9000m, true));
        // Sem linhas, valem a meta por categoria (Budget) e, só então, a agregada: nunca as duas somadas.
        Assert.Equal((5000m, "Budget"), MonthlyPlanCalculator.PlannedConsumption(withoutLines, 5000m, 9000m, true));
        Assert.Equal((9000m, "AggregateTarget"), MonthlyPlanCalculator.PlannedConsumption(withoutLines, null, 9000m, true));
        Assert.Equal((null, null), MonthlyPlanCalculator.PlannedConsumption(withoutLines, 5000m, 9000m, false));
        // Só limites: o total do plano é o próprio limite e o consumo continua vindo da meta por categoria.
        Assert.Equal(2500m, limitsOnly.Expense);
        Assert.Equal((2500m, "Budget"), MonthlyPlanCalculator.PlannedConsumption(limitsOnly, 2500m, null, true));
    }

    // ---- Dias úteis e receita por hora ---------------------------------------------------------------------------------------------

    [Fact]
    public void BusinessDaysAreWeekdaysWithoutTheConfiguredHolidaysAndNonWorkingDays()
    {
        // Outubro de 2026 tem 31 dias e 22 dias de segunda a sexta.
        var plain = MonthlyPlanCalculator.CountDays(Oct, new HashSet<DateOnly>());
        // 12/10 (segunda) feriado, 15/10 (quinta) dia não trabalhado; 17/10 é sábado e não reduz nada.
        var configured = MonthlyPlanCalculator.CountDays(Oct, new HashSet<DateOnly> { new(2026, 10, 12), new(2026, 10, 15), new(2026, 10, 17) });

        Assert.Equal((31, 22, 0, 22), plain);
        Assert.Equal((31, 22, 2, 20), configured);
    }

    [Fact]
    public void FebruaryOfALeapYearAndAMonthWithOnlyHolidaysAreHandled()
    {
        Assert.Equal((29, 21, 0, 21), MonthlyPlanCalculator.CountDays(new DateOnly(2028, 2, 1), new HashSet<DateOnly>()));
        var all = Enumerable.Range(1, 31).Select(d => new DateOnly(2026, 10, d)).ToHashSet();
        Assert.Equal((31, 22, 22, 0), MonthlyPlanCalculator.CountDays(Oct, all));
    }

    [Theory]
    [InlineData("150.00", "8", 20, "24000.00")]
    [InlineData("133.33", "7.5", 18, "17999.55")]
    [InlineData("0.10", "0.33", 3, "0.10")]
    [InlineData("99.99", "8", 21, "16798.32")]
    public void TheHourlyAmountIsRateTimesHoursTimesBillableDaysInExactCents(string rate, string hours, int days, string expected)
    {
        var amount = MonthlyPlanCalculator.HourlyAmount(decimal.Parse(rate, System.Globalization.CultureInfo.InvariantCulture), decimal.Parse(hours, System.Globalization.CultureInfo.InvariantCulture), days);

        Assert.Equal(expected, MonthlyPlanCalculator.Cents(amount));
    }

    [Fact]
    public void NotEveryBusinessDayIsBillableSoTheBillableDaysAreInformedAndBoundedByTheCalendar()
    {
        Assert.Empty(MonthlyPlanCalculator.ValidateHourly(150m, 8m, 15, 20));
        Assert.Empty(MonthlyPlanCalculator.ValidateHourly(150m, 8m, 20, 20));

        var errors = MonthlyPlanCalculator.ValidateHourly(0m, 25m, 21, 20);
        Assert.Equal(["billableDays", "hourlyRate", "hoursPerDay"], errors.Keys.Order(StringComparer.Ordinal));
        Assert.Contains("20 dia(s) útil(eis)", errors["billableDays"][0]);

        Assert.Contains("billableDays", MonthlyPlanCalculator.ValidateHourly(150m, 8m, null, 20).Keys);
        Assert.Contains("billableDays", MonthlyPlanCalculator.ValidateHourly(150m, 8m, 0, 20).Keys);
        Assert.Contains("hoursPerDay", MonthlyPlanCalculator.ValidateHourly(150m, 8.005m, 5, 20).Keys);
        Assert.Contains("hourlyRate", MonthlyPlanCalculator.ValidateHourly(10.001m, 8m, 5, 20).Keys);
    }

    // ---- Risco e cobertura ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void ANegativeProjectedBalanceIsHighRiskAndOverridesOtherSignals()
    {
        var (level, reasons) = MonthlyPlanCalculator.Risk("NegativeProjectedBalance", "Future", 1000m, 1500m, 500m, -200m, true);

        Assert.Equal("High", level);
        Assert.Contains(MonthlyPlanCalculator.RiskReasons.NegativeProjectedBalance, reasons);
        Assert.Contains(MonthlyPlanCalculator.RiskReasons.ProjectedExpenseAbovePlan, reasons);
    }

    [Fact]
    public void ProjectedExpenseAboveThePlanOrACategoryLimitExceededIsMediumRisk()
    {
        Assert.Equal("Medium", MonthlyPlanCalculator.Risk("WithinKnownCommitments", "Future", 1000m, 1000.01m, null, 0m, true).Level);
        Assert.Equal("Medium", MonthlyPlanCalculator.Risk("AboveTarget", "Future", null, 0m, null, 0m, true).Level);
        Assert.Equal("Low", MonthlyPlanCalculator.Risk("WithinKnownCommitments", "Future", 1000m, 1000m, null, 0m, true).Level);
    }

    [Fact]
    public void ThePlannedResultIsOnlyComparedInTheCurrentMonthBecauseFutureIncomeIsNeverInvented()
    {
        // Mês futuro: o resultado projetado só tem os compromissos conhecidos, então não é comparado com o planejado.
        Assert.Equal("Low", MonthlyPlanCalculator.Risk("WithinKnownCommitments", "Future", null, 0m, 4000m, -300m, true).Level);
        var current = MonthlyPlanCalculator.Risk("WithinKnownCommitments", "Current", null, 0m, 4000m, 3999.99m, true);
        Assert.Equal("Medium", current.Level);
        Assert.Equal(MonthlyPlanCalculator.RiskReasons.ResultBelowPlan, Assert.Single(current.Reasons));
    }

    [Fact]
    public void WithoutBalanceOrCompleteCoverageTheRiskIsUnknownNeverLow()
    {
        var unavailable = MonthlyPlanCalculator.Risk("Unavailable", "Future", null, 0m, null, 0m, true);
        var incomplete = MonthlyPlanCalculator.Risk("WithinKnownCommitments", "Future", null, 0m, null, 0m, false);

        Assert.Equal(("Unknown", MonthlyPlanCalculator.RiskReasons.BalanceUnavailable), (unavailable.Level, unavailable.Reasons.Single()));
        Assert.Equal(("Unknown", MonthlyPlanCalculator.RiskReasons.IncompleteCoverage), (incomplete.Level, incomplete.Reasons.Single()));
        Assert.True(MonthlyPlanCalculator.RiskRank("High") > MonthlyPlanCalculator.RiskRank("Medium") && MonthlyPlanCalculator.RiskRank("Medium") > MonthlyPlanCalculator.RiskRank("Unknown")
            && MonthlyPlanCalculator.RiskRank("Unknown") > MonthlyPlanCalculator.RiskRank("Low"));
    }

    [Fact]
    public void ObligationsCoverageComesFromTheProjectedBalanceOfTheAgenda()
    {
        Assert.Equal("Covered", MonthlyPlanCalculator.ObligationsCoverage(500m, 120m, null));
        Assert.Equal("Shortfall", MonthlyPlanCalculator.ObligationsCoverage(500m, -1m, new DateOnly(2026, 10, 20)));
        Assert.Equal("Shortfall", MonthlyPlanCalculator.ObligationsCoverage(-10m, -10m, null));
        Assert.Equal("Unknown", MonthlyPlanCalculator.ObligationsCoverage(null, null, null));
    }

    [Fact]
    public void TheMoneyHelpersFormatExactCentsWithADotAndKeepNullsAsNull()
    {
        Assert.Equal("1234.50", MonthlyPlanCalculator.Cents(1234.5m));
        Assert.Equal("-0.30", MonthlyPlanCalculator.Cents(-0.3m));
        Assert.Null(MonthlyPlanCalculator.CentsOrNull(null));
        Assert.Equal("0.00", MonthlyPlanCalculator.CentsOrNull(0m));
    }

    // ---- Base de consumo compartilhada com o Dashboard (refatoração da T10.1) ------------------------------------------------------

    [Fact]
    public void TheSharedConsumptionBaseKeepsCardInvoicePaymentsOutOfTheBankExpenseAndCardObligationsOutOfThePending()
    {
        var slices = new List<CardSlice> { new(Guid.NewGuid(), "Nubank", "PJ", Pf, 700m, 120m, 80m, 0m) };

        var basis = PlanActualCalculator.Consumption(realizedExpense: 1500m, cardPayments: 400m, pendingExpense: 1000m, cardOutstanding: 300m, slices, pendingBankExpense: 50m);

        Assert.Equal((1100m, 700m, 1800m), (basis.BankExpenses, basis.CardConfirmed, basis.Realized));
        Assert.Equal((700m, 50m, 120m, 80m, 950m), (basis.PendingNonCard, basis.PendingBank, basis.CardPendingKnown, basis.CardEstimated, basis.Pending));
        // Sem obrigação de cartão informada, nada é descontado dos pendentes.
        Assert.Equal(1000m, PlanActualCalculator.Consumption(1500m, 400m, 1000m, null, [], 0m).PendingNonCard);
    }
}
