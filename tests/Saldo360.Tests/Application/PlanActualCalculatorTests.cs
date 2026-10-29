using Saldo360.Application.CreditCards.Projection;
using Saldo360.Application.Dashboard.PlanActual;
using Saldo360.Application.Pluggy;

namespace Saldo360.Tests.Application;

/// <summary>T10.1: fórmulas do Dashboard Planejado × Projetado × Realizado. Regras puras, sem banco, rede ou relógio.</summary>
[Trait("Category", "CORE")]
[Trait("Area", "Dashboard")]
public sealed class PlanActualCalculatorTests
{
    private static readonly DateOnly Today = new(2026, 10, 29);
    private static readonly DateOnly Oct = new(2026, 10, 1);
    private static readonly IReadOnlyList<ComponentDto> NoComponents = [];

    // ---- Indicadores: planejado, projetado, realizado -----------------------------------------------------------------------

    [Fact]
    public void WithoutAPlanTheIndicatorIsNotDefinedAndNeverAFictionalZero()
    {
        var indicator = PlanActualCalculator.Indicator(null, "PlannedTransactions", 1500m, 200m, NoComponents, [PlanActualCalculator.Reasons.PlanNotDefined]);

        Assert.Equal(("NotDefined", null, null, null, null, null), (indicator.PlannedState, indicator.Planned, indicator.RealizedDifference, indicator.ProjectedDifference, indicator.ExecutionPercent, indicator.PlannedSource));
        // Receita/despesa realizada continua visível, e a falta de plano não é uma ressalva sobre os dados.
        Assert.Equal(("1500.00", "1700.00", "200.00", "Complete"), (indicator.Realized, indicator.Projected, indicator.Pending, indicator.State));
    }

    [Fact]
    public void ThePlanIsComparedWithTheRealizedAndWithTheProjectedFigureWithExactDecimals()
    {
        var indicator = PlanActualCalculator.Indicator(1000m, "Budget", 860m, 200m, NoComponents, []);

        Assert.Equal(("Defined", "1000.00", "860.00", "1060.00"), (indicator.PlannedState, indicator.Planned, indicator.Realized, indicator.Projected));
        Assert.Equal(("-140.00", "60.00", "86.0", "106.0", "Budget"), (indicator.RealizedDifference, indicator.ProjectedDifference, indicator.ExecutionPercent, indicator.ProjectedPercent, indicator.PlannedSource));
    }

    [Fact]
    public void DecimalSumsStayExactAndAZeroPlanHasNoPercentage()
    {
        var exact = PlanActualCalculator.Indicator(0.30m, "Budget", 0.1m, 0.2m, NoComponents, []);
        var zero = PlanActualCalculator.Indicator(0m, "Budget", 10m, 0m, NoComponents, []);

        Assert.Equal(("0.30", "0.30", "0.00"), (exact.Planned, exact.Projected, exact.ProjectedDifference));
        Assert.Equal(("Defined", "10.00", null), (zero.PlannedState, zero.RealizedDifference, zero.ExecutionPercent));
    }

    [Fact]
    public void CoverageCaveatsMakeTheIndicatorPartialButAMissingPlanDoesNot()
    {
        var partial = PlanActualCalculator.Indicator(100m, "Budget", 1m, 0m, NoComponents, [PlanActualCalculator.Reasons.MovementsInReview]);
        var notPlanned = PlanActualCalculator.Indicator(null, null, 1m, 0m, NoComponents, [PlanActualCalculator.Reasons.PlanNotDefined, PlanActualCalculator.Reasons.PlanNotAttributable]);

        Assert.Equal("Partial", partial.State);
        Assert.Equal("Complete", notPlanned.State);
    }

    [Theory]
    [InlineData("2026-10-01", "Current")]
    [InlineData("2026-09-01", "Past")]
    [InlineData("2026-11-01", "Future")]
    public void ThePeriodKindFollowsTheBusinessDate(string month, string expected) => Assert.Equal(expected, PlanActualCalculator.PeriodKind(DateOnly.Parse(month), Today));

    // ---- Cheque especial -----------------------------------------------------------------------------------------------

    private static OverdraftObservation Obs(string asOf, decimal balance, int hour = 20) =>
        new(DateOnly.Parse(asOf), balance, new DateTimeOffset(DateOnly.Parse(asOf).ToDateTime(new TimeOnly(hour, 0)), TimeSpan.Zero));

    private static OverdraftInput Input(IReadOnlyList<OverdraftObservation> history, decimal latestBalance, DateOnly? month = null, decimal? contracted = 16100m, decimal? closing = null, decimal? difference = 0m)
    {
        var latest = history.OrderBy(h => h.ObservedAt).Last();
        return new(Guid.NewGuid(), "Itaú PF", contracted, Math.Max(0m, -latestBalance), latestBalance, latest.ObservedAt, latest.AsOf, history, month ?? Oct, Today, closing, difference);
    }

    [Fact]
    public void ARecoveryIsTheReductionOfTheUsedLimitBetweenTheStartAndTheLastObservation()
    {
        var result = PlanActualCalculator.Overdraft(Input([Obs("2026-09-30", -15000m), Obs("2026-10-28", -13811.51m)], -13811.51m, closing: -13811.51m));

        Assert.Equal(1188.49m, result.Effective);
        Assert.Equal(("1188.49", "Recovered", "15000.00", "13811.51"), (result.Dto.EffectiveRecovery, result.Dto.Direction, result.Dto.UsedAtStart, result.Dto.UsedLatest));
        Assert.False(result.Dto.StartApproximate);
        Assert.Equal("Complete", PlanActualCalculator.ConsolidateOverdraft([result], "Current").State);
    }

    [Fact]
    public void ARiseInTheUsedLimitIsANegativeRecovery()
    {
        var result = PlanActualCalculator.Overdraft(Input([Obs("2026-09-30", -5000m), Obs("2026-10-28", -8000m)], -8000m));

        Assert.Equal(("-3000.00", "Increased"), (result.Dto.EffectiveRecovery, result.Dto.Direction));
    }

    [Fact]
    public void AnUnchangedUseIsStableAndAFullyRecoveredLimitProjectsNoInventedRecovery()
    {
        var stable = PlanActualCalculator.Overdraft(Input([Obs("2026-09-30", -1000m), Obs("2026-10-28", -1000m)], -1000m));
        var recovered = PlanActualCalculator.Overdraft(Input([Obs("2026-09-30", -1000m), Obs("2026-10-28", 250m)], 250m, closing: 250m));

        Assert.Equal(("0.00", "Stable"), (stable.Dto.EffectiveRecovery, stable.Dto.Direction));
        Assert.True(recovered.Dto.FullyRecovered);
        Assert.Equal(("1000.00", "0.00", "0.00", "0.00"), (recovered.Dto.EffectiveRecovery, recovered.Dto.UsedLatest, recovered.Dto.ProjectedRecoveryFromNow, recovered.Dto.ProjectedUsedAtEnd));
    }

    [Fact]
    public void WithoutAComparableStartingObservationTheHistoryIsInsufficientAndTheCurrentUseIsPreserved()
    {
        var onlyToday = PlanActualCalculator.Overdraft(Input([Obs("2026-10-28", -13811.51m)], -13811.51m));
        var stale = PlanActualCalculator.Overdraft(Input([Obs("2026-09-20", -15000m), Obs("2026-10-28", -13811.51m)], -13811.51m));

        foreach (var result in new[] { onlyToday, stale })
        {
            Assert.Null(result.Effective);
            Assert.Equal(("InsufficientHistory", null, null, "13811.51"), (result.Dto.State, result.Dto.EffectiveRecovery, result.Dto.Direction, result.Dto.UsedLatest));
            Assert.Contains(PlanActualCalculator.Reasons.InsufficientHistory, result.Dto.Reasons);
        }
    }

    [Fact]
    public void TheSameObservationIsNeverBothStartAndEnd()
    {
        // A única observação do mês cai nos primeiros dias: serviria de início, mas é também a última — nada foi comparado.
        var result = PlanActualCalculator.Overdraft(Input([Obs("2026-10-02", -4000m)], -4000m));

        Assert.Null(result.Effective);
        Assert.Equal("InsufficientHistory", result.Dto.State);
    }

    [Fact]
    public void ANearbyObservationServesAsAnApproximateStartAndIsMarkedAsSuch()
    {
        var (before, beforeApprox) = PlanActualCalculator.PickStart([Obs("2026-09-28", -900m)], Oct);
        var (after, afterApprox) = PlanActualCalculator.PickStart([Obs("2026-10-02", -700m)], Oct);
        var (none, _) = PlanActualCalculator.PickStart([Obs("2026-10-09", -700m)], Oct);
        var (exact, exactApprox) = PlanActualCalculator.PickStart([Obs("2026-09-30", -800m, 10), Obs("2026-09-30", -750m, 22)], Oct);

        Assert.Equal((-900m, true), (before!.Balance, beforeApprox));
        Assert.Equal((-700m, true), (after!.Balance, afterApprox));
        Assert.Null(none);
        // Duas observações no último dia: vale a mais recente do dia.
        Assert.Equal((-750m, false), (exact!.Balance, exactApprox));
    }

    [Fact]
    public void AnApproximateStartIsReportedThroughTheResult()
    {
        var result = PlanActualCalculator.Overdraft(Input([Obs("2026-09-28", -900m), Obs("2026-10-28", -400m)], -400m));

        Assert.Equal((500m, true, new DateOnly(2026, 9, 28)), (result.Effective, result.Dto.StartApproximate, result.Dto.StartAsOf));
    }

    [Fact]
    public void TheHistoryIsOnlyUsedWhenTheObservedBalanceReproducesTheReportedUse()
    {
        // O banco informa 5.000 de uso, mas o saldo observado é positivo: o saldo não reproduz a utilização, então não há série comparável.
        var input = Input([Obs("2026-09-30", 100m), Obs("2026-10-28", 100m)], 100m) with { UsedLatest = 5000m };

        var result = PlanActualCalculator.Overdraft(input);

        Assert.Null(result.Effective);
        Assert.Contains(PlanActualCalculator.Reasons.NotDerivableFromBalance, result.Dto.Reasons);
        Assert.Equal("5000.00", result.Dto.UsedLatest);
    }

    [Fact]
    public void TheProjectedRecoveryUsesTheProjectedClosingBalanceCorrectedByTheBankDifference()
    {
        var reconciled = PlanActualCalculator.Overdraft(Input([Obs("2026-09-30", -15000m), Obs("2026-10-28", -13811.51m)], -13811.51m, closing: -10000m, difference: 0m));
        var divergent = PlanActualCalculator.Overdraft(Input([Obs("2026-09-30", -15000m), Obs("2026-10-28", -13811.51m)], -13811.51m, closing: -10000m, difference: -500m));
        var unavailable = PlanActualCalculator.Overdraft(Input([Obs("2026-09-30", -15000m), Obs("2026-10-28", -13811.51m)], -13811.51m, closing: null));

        Assert.Equal(("10000.00", "3811.51", "5000.00"), (reconciled.Dto.ProjectedUsedAtEnd, reconciled.Dto.ProjectedRecoveryFromNow, reconciled.Dto.ProjectedRecovery));
        Assert.Equal(("10500.00", "-10500.00"), (divergent.Dto.ProjectedUsedAtEnd, divergent.Dto.ProjectedBalanceAtEnd));
        Assert.Contains(PlanActualCalculator.Reasons.ProjectionAdjustedByDifference, divergent.Dto.Reasons);
        Assert.Null(unavailable.Dto.ProjectedRecovery);
        Assert.Contains(PlanActualCalculator.Reasons.ProjectionUnavailable, unavailable.Dto.Reasons);
        Assert.Equal("Partial", unavailable.Dto.State);
    }

    [Fact]
    public void AProjectionBeyondTheContractedLimitIsFlaggedAndNeverAddedToTheBalance()
    {
        var result = PlanActualCalculator.Overdraft(Input([Obs("2026-09-30", -1000m), Obs("2026-10-28", -2000m)], -2000m, contracted: 3000m, closing: -4000m));

        Assert.True(result.Dto.ExceedsLimit);
        Assert.Equal("-4000.00", result.Dto.ProjectedBalanceAtEnd);
    }

    [Fact]
    public void ThePastPeriodComparesStartAndEndOfThatMonthAndNeverProjects()
    {
        var september = new DateOnly(2026, 9, 1);
        var history = new[] { Obs("2026-08-31", -3000m), Obs("2026-09-30", -2500m), Obs("2026-10-28", -2000m) };

        var past = PlanActualCalculator.Overdraft(Input(history, -2000m, september, closing: -1000m));
        var staleEnd = PlanActualCalculator.Overdraft(Input([Obs("2026-08-31", -3000m), Obs("2026-09-15", -2800m), Obs("2026-10-28", -2000m)], -2000m, september));

        Assert.Equal(("500.00", null), (past.Dto.EffectiveRecovery, past.Dto.ProjectedRecovery));
        Assert.Null(staleEnd.Effective);
    }

    [Fact]
    public void AFuturePeriodHasOnlyAProjectionAndNoEffectiveRecovery()
    {
        var november = new DateOnly(2026, 11, 1);

        var result = PlanActualCalculator.Overdraft(Input([Obs("2026-09-30", -15000m), Obs("2026-10-28", -13000m)], -13000m, november, closing: -9000m));

        Assert.Null(result.Dto.EffectiveRecovery);
        Assert.Equal(("4000.00", "Partial"), (result.Dto.ProjectedRecoveryFromNow, result.Dto.State));
        Assert.Contains(PlanActualCalculator.Reasons.FuturePeriod, result.Dto.Reasons);
    }

    [Fact]
    public void TheConsolidationOfOverdraftAccountsIsPartialWhenOneLacksCoverage()
    {
        var covered = PlanActualCalculator.Overdraft(Input([Obs("2026-09-30", -15000m), Obs("2026-10-28", -13000m)], -13000m, closing: -12000m));
        var missing = PlanActualCalculator.Overdraft(Input([Obs("2026-10-28", -800m)], -800m, closing: -700m));

        var total = PlanActualCalculator.ConsolidateOverdraft([covered, missing], "Current");

        Assert.Equal(("Partial", "2000.00", "15000.00", "13800.00"), (total.State, total.EffectiveRecovery, total.UsedAtStart, total.UsedLatest));
        Assert.Equal(2, total.Accounts.Count);
        Assert.False(PlanActualCalculator.ConsolidateOverdraft([], "Current").Eligible);
    }

    // ---- Cartões ---------------------------------------------------------------------------------------------------------

    private static CardInfo Card(Guid id, string name, Guid? payer) =>
        new(id, name, "Banco", "1234", "PF", "Pluggy", 5, 15, 10000m, 8000m, "Bank", payer, "Itaú PF", "PF", true, "Active", null);

    private static CardInvoiceProjection Invoice(string competence, InvoicePhase phase, decimal composition, decimal pendingKnown = 0m, decimal known = 0m, decimal estimated = 0m, decimal? bankTotal = null) =>
        new(Guid.Empty, Guid.NewGuid(), competence, new DateOnly(2026, 9, 6), new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 15), phase,
            phase == InvoicePhase.Closed ? ObligationBasis.BankTotal : ObligationBasis.ProjectedOpen, phase != InvoicePhase.Closed, false, bankTotal, null, composition, pendingKnown, known, estimated,
            composition + pendingKnown + known + estimated, phase == InvoicePhase.Closed ? bankTotal ?? composition : composition + pendingKnown + known + estimated, 0m, 0m, InvoicePaymentState.Unpaid,
            "NotVerified", null, null, null, 0, 0, 0, []);

    private static CardProjection Projection(Guid id, string name, Guid? payer, params CardInvoiceProjection[] invoices) =>
        new(Card(id, name, payer), new CardCycleSchedule(5, 15, CardCycleSchedule.BankHistory, true), invoices, [], [], new CardCoverage(true, []));

    [Fact]
    public void ACardSliceSeparatesConfirmedPendingAndEstimatedConsumptionOfTheInvoiceMonth()
    {
        var payer = Guid.NewGuid();
        var projection = Projection(Guid.NewGuid(), "Cartão", payer,
            Invoice("2026-10", InvoicePhase.Closed, 3900m, bankTotal: 4300m),
            Invoice("2026-11", InvoicePhase.Open, 1000m, pendingKnown: 200m, known: 300m, estimated: 50m),
            Invoice("2026-12", InvoicePhase.Future, 0m, known: 100m));

        var october = PlanActualCalculator.Slice(projection, [], "2026-10");
        var november = PlanActualCalculator.Slice(projection, [], "2026-11");

        // Fatura fechada vale o total do banco, sem somar de novo os componentes; a aberta separa o pendente conhecido do estimado.
        Assert.Equal((4300m, 0m, 0m, 4300m), (october.Confirmed, october.PendingKnown, october.Estimated, october.Projected));
        Assert.Equal((1000m, 500m, 50m, 1550m), (november.Confirmed, november.PendingKnown, november.Estimated, november.Projected));
    }

    [Fact]
    public void AnInstallmentOnlyCountsInTheMonthOfItsInvoiceAndTheBankChargesAreHighlightedNotAdded()
    {
        var projection = Projection(Guid.NewGuid(), "Cartão", null, Invoice("2026-10", InvoicePhase.Closed, 900m, bankTotal: 1000m), Invoice("2026-11", InvoicePhase.Open, 0m, known: 250m));
        IReadOnlyList<PluggyBillFact> bills = [new("b1", new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 15), 1000m, "BRL") { FinanceChargesTotal = 42.5m }];

        var october = PlanActualCalculator.Slice(projection, bills, "2026-10");
        var november = PlanActualCalculator.Slice(projection, bills, "2026-11");

        Assert.Equal((1000m, 42.5m), (october.Confirmed, october.Charges));
        Assert.Equal((0m, 250m, 0m), (november.Confirmed, november.PendingKnown, november.Charges));
    }

    [Fact]
    public void TheCardTargetShowsRemainingExcessAndPercentagesAndNeverRaisesTheTarget()
    {
        var payer = Guid.NewGuid();
        CardSlice Slice(string name, decimal confirmed, decimal pending, decimal estimated) => new(Guid.NewGuid(), name, "PF", payer, confirmed, pending, estimated, 0m);
        IReadOnlyList<CardSlice> slices = [Slice("A", 3000m, 300m, 0m), Slice("B", 1300m, 200m, 100m)];

        var dto = PlanActualCalculator.CardTarget(slices, 4500m, "CardTarget", false, [payer], []);

        Assert.Equal(("4500.00", "4300.00", "500.00", "100.00", "4900.00"), (dto.Target, dto.Confirmed, dto.PendingKnown, dto.Estimated, dto.Projected));
        Assert.Equal(("0.00", "400.00", "0.00", "95.6", "108.9"), (dto.Remaining, dto.ExcessProjected, dto.ExcessConfirmed, dto.ConfirmedPercent, dto.ProjectedPercent));
        Assert.Equal(("Defined", "Complete"), (dto.PlannedState, dto.State));
    }

    [Fact]
    public void WithoutATargetTheCardConsumptionIsStillShownAndTheTargetIsNotDefined()
    {
        var dto = PlanActualCalculator.CardTarget([new(Guid.NewGuid(), "A", "PF", null, 100m, 0m, 0m, 0m)], null, null, false, [], [PlanActualCalculator.Reasons.PlanNotDefined]);
        var none = PlanActualCalculator.CardTarget([], 5000m, "CardTarget", false, [], [PlanActualCalculator.Reasons.NoCards]);

        Assert.Equal(("NotDefined", null, null, null, "100.00"), (dto.PlannedState, dto.Target, dto.Remaining, dto.ExcessProjected, dto.Projected));
        Assert.False(none.Applicable);
    }

    [Fact]
    public void ExcessOverTheTargetIsReportedForConfirmedAndForProjectedConsumption()
    {
        var dto = PlanActualCalculator.CardTarget([new(Guid.NewGuid(), "A", "PF", null, 5700m, 0m, 0m, 0m)], 5000m, "CardTarget", false, [], []);

        Assert.Equal(("700.00", "700.00", "0.00"), (dto.ExcessConfirmed, dto.ExcessProjected, dto.Remaining));
    }

    // ---- Sincronização ----------------------------------------------------------------------------------------------------

    private static SyncConnectionDto Connection(string status, DateTimeOffset? success, DateTimeOffset? next = null) => new(Guid.NewGuid(), "Conexão", status, success, success, next, false);

    [Fact]
    public void DifferentSynchronizationMomentsAreShownAsARangeNeverAsOneDate()
    {
        var now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var summary = PlanActualCalculator.Sync([Connection("Completed", now.AddHours(-2)), Connection("Completed", now.AddHours(-30))], false, 24, now);

        Assert.Equal((now.AddHours(-30), now.AddHours(-2), false, false), (summary.OldestSuccessAt, summary.LatestSuccessAt, summary.SameMoment, summary.Stale));
    }

    [Fact]
    public void ConnectionsSynchronizedTogetherShareOneMomentAndAnOldOneMakesTheScopeStale()
    {
        var now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var together = PlanActualCalculator.Sync([Connection("Completed", now.AddHours(-2)), Connection("Completed", now.AddHours(-2).AddMinutes(-1))], true, 24, now);
        var stale = PlanActualCalculator.Sync([Connection("Completed", now.AddHours(-2)), Connection("Completed", now.AddHours(-40))], true, 24, now);
        var disabledButRecent = PlanActualCalculator.Sync([Connection("Completed", now.AddHours(-40))], false, 24, now);

        Assert.True(together.SameMoment);
        Assert.True(stale.Stale);
        Assert.False(disabledButRecent.Stale);
    }

    [Fact]
    public void TheNextAttemptOnlyExistsWithTheAutomaticSynchronizationOn()
    {
        var now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        var connections = new[] { Connection("Completed", now.AddHours(-2), now.AddHours(22)), Connection("Completed", now.AddHours(-3), now.AddHours(21)) };

        Assert.Equal(now.AddHours(21), PlanActualCalculator.Sync(connections, true, 24, now).NextAttemptAt);
        Assert.Null(PlanActualCalculator.Sync(connections, false, 24, now).NextAttemptAt);
    }

    [Fact]
    public void TheAggregateStateIsTheWorstOneAndANeverSynchronizedConnectionHasNoOldestDate()
    {
        var now = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

        var failed = PlanActualCalculator.Sync([Connection("Completed", now.AddHours(-1)), Connection("Failed", now.AddHours(-5)), Connection("SourceNotUpdated", now.AddHours(-1))], true, 24, now);
        var never = PlanActualCalculator.Sync([Connection("Completed", now.AddHours(-1)), Connection("NeverSynced", null)], true, 24, now);

        Assert.Equal("Failed", failed.State);
        Assert.Equal(("NeverSynced", null, true), (never.State, never.OldestSuccessAt, never.Stale));
    }
}
