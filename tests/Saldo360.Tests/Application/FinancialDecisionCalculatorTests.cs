using Saldo360.Application.Calendar;
using Saldo360.Application.Dashboard.Overview;

namespace Saldo360.Tests.Application;

/// <summary>T11.1: compromissos restantes, cobertura por conta, margem segura e meta de recuperação (cálculo puro sobre o snapshot da Agenda; relógio e dados fixos).</summary>
[Trait("Category", "CORE")]
[Trait("Area", "Dashboard")]
[Trait("Profile", "FAST")]
public sealed class FinancialDecisionCalculatorTests
{
    private static readonly DateOnly Today = new(2026, 10, 20);
    private static readonly DateOnly First = new(2026, 10, 1);
    private static readonly DateOnly Last = new(2026, 10, 31);
    private static DateOnly D(int day) => new(2026, 10, day);

    private static FinancialCalendarDailyTotals Row(DateOnly date, decimal income = 0m, decimal expense = 0m, decimal transferIn = 0m, decimal transferOut = 0m) => new(date, income, expense, transferIn, transferOut);

    /// <summary>Saldo de referência (opening) em 30/09. <c>inferred</c> entra em Pending E em InferredPending, como o repositório faz.</summary>
    private static FinancialCalendarBalanceSnapshot Snapshot(decimal? opening, IEnumerable<FinancialCalendarDailyTotals>? pending = null, IEnumerable<FinancialCalendarDailyTotals>? cards = null,
        IEnumerable<FinancialCalendarDailyTotals>? debts = null, IEnumerable<FinancialCalendarDailyTotals>? inferred = null, bool cardWithoutPayer = false, bool debtWithoutResponsible = false) =>
        new([new(Guid.NewGuid(), opening, opening is null ? null : D(1).AddDays(-1))], [], (pending ?? []).Concat(inferred ?? []).ToList(), CreditCardObligations: (cards ?? []).ToList(),
            DebtObligations: (debts ?? []).ToList(), InferredPending: (inferred ?? []).ToList(), HasCardObligationsWithoutPayer: cardWithoutPayer, HasDebtObligationsWithoutResponsible: debtWithoutResponsible);

    private static DecisionAccountInput Account(FinancialCalendarBalanceSnapshot snapshot, decimal? limit = null, string name = "Conta", string scope = "PF") => new(Guid.NewGuid(), name, scope, limit, snapshot);

    private static DecisionAccountCoverageDto Coverage(DecisionAccountInput account) => FinancialDecisionCalculator.AccountCoverage(account, Today, Last, false);

    private static DecisionSafeMarginDto Margin(DecisionAccountInput account, decimal? opening, OverviewRecoveryDto? recovery, params string[] caveats) =>
        FinancialDecisionCalculator.SafeMargin(account, Today, First, Last, opening, recovery, caveats);

    private static OverviewRecoveryDto Target(string target, string state = "Fixed", string? projectedRecovery = "1000.00") =>
        new("15000.00", target, state, "0.80", "3125.00", null, D(10), false, null, "-15000.00", Today, "0.00", "-14000.00", projectedRecovery, "40.0", "Critical", null, null);

    // ---- Compromissos ----------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void CommitmentsSumEachSourceOnceAndSeparateOverdueFromTheRestOfTheCompetence()
    {
        var snapshot = Snapshot(1000m, pending: [Row(D(15), expense: 40m), Row(D(25), expense: 100m)], cards: [Row(D(28), expense: 300m)], debts: [Row(D(22), expense: 50m)]);
        var result = FinancialDecisionCalculator.Commitments(snapshot, First, Last, Today, null);
        Assert.Equal("Current", result.Period);
        Assert.Equal("490.00", result.Total); Assert.Equal("40.00", result.Overdue); Assert.Equal("450.00", result.InCompetence);
        Assert.Equal(["Expenses", "CardInvoices", "Debts"], result.ByNature.Select(n => n.Nature));
        Assert.Equal("300.00", result.ByNature.Single(n => n.Nature == "CardInvoices").Total);
        Assert.Equal("Complete", result.Reliability);
    }

    [Fact]
    public void PartiallyPaidInvoiceCountsOnlyWhatIsStillOwed()
    {
        // A projeção dos cartões já entrega o restante (obrigação 1.000 − pago 700 = 300): o compromisso soma exatamente esse restante, nunca a fatura inteira.
        var snapshot = Snapshot(1000m, cards: [Row(D(28), expense: 300m)]);
        Assert.Equal("300.00", FinancialDecisionCalculator.Commitments(snapshot, First, Last, Today, null).Total);
    }

    [Fact]
    public void InferredIncomeAndProjectedRepasseAreNotCommitmentsButRegisteredTransfersAre()
    {
        var snapshot = Snapshot(1000m, pending: [Row(D(25), expense: 100m), Row(D(26), transferOut: 70m)], inferred: [Row(D(24), transferOut: 6000m)]);
        var result = FinancialDecisionCalculator.Commitments(snapshot, First, Last, Today, null);
        Assert.Equal("170.00", result.Total);
        Assert.Equal("70.00", result.ByNature.Single(n => n.Nature == "PlannedTransfers").Total);
    }

    [Fact]
    public void ConsolidatedNetTransfersLeaveNoCommitment()
    {
        // No consolidado as duas pontas se anulam (o repositório entrega o líquido, zero): nenhuma natureza de transferência aparece.
        var result = FinancialDecisionCalculator.Commitments(Snapshot(1000m, pending: [Row(D(25), transferIn: 0m, transferOut: 0m)]), First, Last, Today, null);
        Assert.Equal("0.00", result.Total); Assert.Empty(result.ByNature);
    }

    [Fact]
    public void FutureCompetenceSeparatesWhatFallsBeforeItAndWhatIsOverdue()
    {
        var first = new DateOnly(2026, 12, 1); var last = new DateOnly(2026, 12, 31);
        var snapshot = Snapshot(1000m, pending: [Row(D(15), expense: 10m), Row(new(2026, 11, 20), expense: 20m), Row(new(2026, 12, 5), expense: 30m)]);
        var result = FinancialDecisionCalculator.Commitments(snapshot, first, last, Today, null);
        Assert.Equal("Future", result.Period);
        Assert.Equal("10.00", result.Overdue); Assert.Equal("20.00", result.BeforeCompetence); Assert.Equal("30.00", result.InCompetence); Assert.Equal("60.00", result.Total);
    }

    [Fact]
    public void ClosedCompetenceReportsOnlyWhatIsStillOpenInsideItWithoutClaimingItWasPendingThen()
    {
        var today = new DateOnly(2026, 11, 5);
        var snapshot = Snapshot(1000m, pending: [Row(D(8), expense: 90m), Row(new(2026, 9, 12), expense: 5m), Row(new(2026, 11, 2), expense: 7m)]);
        var result = FinancialDecisionCalculator.Commitments(snapshot, First, Last, today, 1234.50m);
        Assert.Equal("Closed", result.Period);
        Assert.Equal("90.00", result.Total); Assert.Equal("90.00", result.InCompetence); Assert.Equal("0.00", result.Overdue);
        Assert.Equal("1234.50", result.PaidInMonth);
    }

    [Fact]
    public void ObligationsWithoutPayerAreFlaggedAsIncompleteCoverageAndNeverAssigned()
    {
        var snapshot = Snapshot(1000m, cards: [Row(D(28), expense: 300m)], cardWithoutPayer: true, debtWithoutResponsible: true);
        var result = FinancialDecisionCalculator.Commitments(snapshot, First, Last, Today, null);
        Assert.Equal("Preliminary", result.Reliability);
        Assert.Contains("CardCoverageIncomplete", result.Caveats); Assert.Contains("DebtCoverageIncomplete", result.Caveats);
        var coverage = Coverage(Account(snapshot));
        Assert.Equal("Preliminary", coverage.Reliability);
    }

    // ---- Cobertura -------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void AccountThatClosesPositiveButRunsShortBeforeAnObligationIsInsufficient()
    {
        // Saldo 1.000; despesa de 1.500 em 22/10; entrada de 2.000 só em 30/10. Fecha em +1.500, mas em 22/10 chega a −500, sem limite.
        var snapshot = Snapshot(1000m, pending: [Row(D(22), expense: 1500m), Row(D(30), income: 2000m)]);
        var coverage = Coverage(Account(snapshot, limit: 0m));
        Assert.Equal("Insufficient", coverage.State);
        Assert.Equal(D(22), coverage.FirstBeyondLimitDate); Assert.Equal(D(22), coverage.FirstNegativeDate);
        Assert.Equal("-500.00", coverage.MinimumBalance); Assert.Equal("500.00", coverage.Deficit);
    }

    [Fact]
    public void CoveredOnlyThroughOverdraftIsNotCoveredWithOwnFunds()
    {
        var snapshot = Snapshot(1000m, pending: [Row(D(22), expense: 1500m), Row(D(30), income: 2000m)]);
        var coverage = Coverage(Account(snapshot, limit: 1000m));
        Assert.Equal("CoveredWithCredit", coverage.State);
        Assert.Equal("500.00", coverage.CreditNeeded); Assert.Null(coverage.Deficit); Assert.Null(coverage.FirstBeyondLimitDate);
        Assert.Equal(D(22), coverage.FirstNegativeDate);
    }

    [Fact]
    public void ShortfallBeyondTheContractedLimitIsInsufficientWithTheDeficitOverTheLimit()
    {
        var snapshot = Snapshot(1000m, pending: [Row(D(22), expense: 1500m)]);
        var coverage = Coverage(Account(snapshot, limit: 200m));
        Assert.Equal("Insufficient", coverage.State); Assert.Equal("300.00", coverage.Deficit);
    }

    [Fact]
    public void PjAccountWithoutContractedLimitNeverTreatsNegativeBalanceAsFunding()
    {
        var snapshot = Snapshot(100m, pending: [Row(D(22), expense: 150m)]);
        var coverage = Coverage(Account(snapshot, limit: null, scope: "PJ"));
        Assert.Equal("Insufficient", coverage.State); Assert.Equal(D(22), coverage.FirstBeyondLimitDate);
        Assert.Contains("OverdraftLimitNotInformed", coverage.Caveats);
    }

    [Fact]
    public void PositiveEveryDayIsCoveredWithOwnFunds()
    {
        var coverage = Coverage(Account(Snapshot(1000m, pending: [Row(D(22), expense: 400m)]), limit: 500m));
        Assert.Equal("Covered", coverage.State); Assert.Equal("600.00", coverage.MinimumBalance); Assert.Equal("Complete", coverage.Reliability);
    }

    [Fact]
    public void MissingReferenceBalanceMakesCoverageUnavailableNotZero()
    {
        var coverage = Coverage(Account(Snapshot(null, pending: [Row(D(22), expense: 400m)])));
        Assert.Equal("Unavailable", coverage.State); Assert.Null(coverage.MinimumBalance); Assert.NotNull(coverage.UnavailableReason);
    }

    [Fact]
    public void CoverageThatDependsOnInferredIncomeIsFlaggedAndGuaranteedStateIsWorse()
    {
        var snapshot = Snapshot(100m, pending: [Row(D(22), expense: 500m)], inferred: [Row(D(21), income: 3000m)]);
        var coverage = Coverage(Account(snapshot, limit: 0m));
        Assert.Equal("Covered", coverage.State); Assert.Equal("Insufficient", coverage.GuaranteedState); Assert.True(coverage.DependsOnInferredIncome);
        Assert.Equal("Preliminary", coverage.Reliability); Assert.Contains("DependsOnInferredIncome", coverage.Caveats);
    }

    [Fact]
    public void ConsolidatedShowsEachAccountRiskAndNeverOffsetsReservedPjCashAgainstPfDebt()
    {
        var pj = Account(Snapshot(50000m), limit: 0m, name: "PJ", scope: "PJ");
        var pf = Account(Snapshot(-800m, pending: [Row(D(22), expense: 100m)]), limit: 500m, name: "PF");
        var accounts = new[] { pj, pf }.Select(Coverage).ToList();
        var aggregate = FinancialDecisionCalculator.Coverage([pj, pf], accounts, Today, First, Last, 100m);
        Assert.Equal("Covered", accounts[0].State);
        Assert.Equal("Insufficient", accounts[1].State); // −900 passa do limite de 500, mesmo com 50.000 na PJ
        Assert.Equal("Insufficient", aggregate.State); Assert.Equal(2, aggregate.Accounts.Count); Assert.Equal("-900.00", aggregate.MinimumBalance);
    }

    [Fact]
    public void ExpectedInflowsMarkInferredEntriesSeparately()
    {
        var snapshot = Snapshot(100m, pending: [Row(D(25), income: 400m)], inferred: [Row(D(27), income: 3000m)]);
        var input = Account(snapshot);
        var coverage = FinancialDecisionCalculator.Coverage([input], [Coverage(input)], Today, First, Last, 0m);
        Assert.Collection(coverage.ExpectedInflows,
            first => { Assert.Equal("Registered", first.Certainty); Assert.Equal("400.00", first.Amount); },
            second => { Assert.Equal("Inferred", second.Certainty); Assert.Equal("3000.00", second.Amount); });
    }

    // ---- Margem segura ---------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void MarginIsLimitedByTheSmallestDailySlackBeforeTheRecoveryTargetSlack()
    {
        // Exemplo da especificação: abre usando 15.000, meta 2.500 (uso máximo no fechamento 12.500), fecha projetado em −11.000 → folga de 1.500 na meta.
        // Mas o vencimento de 22/10 leva o saldo a −19.400 com limite de 20.000: só 600 de folga diária. Margem = 600.
        var snapshot = Snapshot(-15000m, pending: [Row(D(22), expense: 4400m), Row(D(28), income: 8400m)]);
        var margin = Margin(Account(snapshot, limit: 20000m), -15000m, Target("2500.00"));
        Assert.Equal("Calculated", margin.State); Assert.Equal("600.00", margin.Amount); Assert.Equal("DailyCoverage", margin.BindingConstraint);
        Assert.Equal("1500.00", margin.Constraints.Single(c => c.Kind == "RecoveryTarget").Slack);
    }

    [Fact]
    public void MarginIsLimitedByTheOriginalRecoveryTargetWhenItIsTheTightestConstraint()
    {
        var snapshot = Snapshot(-15000m, pending: [Row(D(25), income: 4000m)]);
        var margin = Margin(Account(snapshot, limit: 20000m), -15000m, Target("2500.00"));
        Assert.Equal("1500.00", margin.Amount); Assert.Equal("RecoveryTarget", margin.BindingConstraint);
        Assert.True(decimal.Parse(margin.Constraints.Single(c => c.Kind == "DailyCoverage").Slack!) > 1500m); // o limite disponível (5.000) NÃO é a margem
    }

    [Fact]
    public void MarginDoesNotCountInferredIncomeAsGuaranteed()
    {
        var registered = Snapshot(-15000m, pending: [Row(D(25), income: 4000m)]);
        var withInferred = Snapshot(-15000m, pending: [Row(D(25), income: 4000m)], inferred: [Row(D(26), income: 9000m)]);
        var baseline = Margin(Account(registered, limit: 20000m), -15000m, Target("2500.00"));
        var guess = Margin(Account(withInferred, limit: 20000m), -15000m, Target("2500.00"));
        Assert.Equal(baseline.Amount, guess.Amount);
        Assert.Contains("InferredIncomeExcluded", guess.Notes);
    }

    [Fact]
    public void AccountWithoutNegativeOpeningUsesOnlyTheDailyCoverageAndNeverTheLimitAsOwnFunds()
    {
        var snapshot = Snapshot(1000m, pending: [Row(D(22), expense: 400m)]);
        var margin = Margin(Account(snapshot, limit: 0m, scope: "PJ"), 1000m, null);
        Assert.Equal("600.00", margin.Amount);
        Assert.Equal("AccountDidNotOpenNegative", margin.Constraints.Single(c => c.Kind == "RecoveryTarget").Reason);
    }

    [Fact]
    public void OverdraftLimitIsNeverOwnFundsForAnAccountThatDidNotOpenOverdrawn()
    {
        var snapshot = Snapshot(1000m, pending: [Row(D(22), expense: 400m)]);
        var margin = Margin(Account(snapshot, limit: 20000m), 1000m, null);
        Assert.Equal("600.00", margin.Amount); // 20.600 (saldo + limite) NÃO é margem
        Assert.Equal("OwnFundsOnly", margin.Constraints.Single(c => c.Kind == "DailyCoverage").Reason);
    }

    [Fact]
    public void MarginIsZeroWhenThereIsNoSlackNeverNegative()
    {
        var snapshot = Snapshot(100m, pending: [Row(D(22), expense: 300m)]);
        var margin = Margin(Account(snapshot, limit: 0m), 100m, null);
        Assert.Equal("0.00", margin.Amount); Assert.Equal("Calculated", margin.State);
    }

    [Fact]
    public void MissingTargetBalanceOrScopeMakeTheMarginUnavailableWithAReason()
    {
        var snapshot = Snapshot(-15000m, pending: [Row(D(25), income: 4000m)]);
        var noTarget = Margin(Account(snapshot, limit: 20000m), -15000m, null);
        Assert.Equal("Unavailable", noTarget.State); Assert.Null(noTarget.Amount); Assert.Contains("RecoveryTargetUnavailable", noTarget.Reasons);
        var pendingTarget = Margin(Account(snapshot, limit: 20000m), -15000m, Target("2500.00", state: "Pending"));
        Assert.Equal("Unavailable", pendingTarget.State);
        var noBalance = Margin(Account(Snapshot(null)), null, null);
        Assert.Equal("Unavailable", noBalance.State); Assert.Contains("OpeningBalanceMissing", noBalance.Reasons);
        var consolidated = FinancialDecisionCalculator.SafeMargin(null, Today, First, Last, null, null, []);
        Assert.Equal("Unavailable", consolidated.State); Assert.Contains("ConsolidatedScope", consolidated.Reasons);
        var closed = FinancialDecisionCalculator.SafeMargin(Account(snapshot), new DateOnly(2026, 11, 5), First, Last, -15000m, Target("2500.00"), []);
        Assert.Contains("CompetenceNotCurrent", closed.Reasons);
    }

    [Fact]
    public void ProvisionalTargetOrSourceCaveatsMakeTheMarginPreliminary()
    {
        var snapshot = Snapshot(-15000m, pending: [Row(D(25), income: 4000m)]);
        var provisional = Margin(Account(snapshot, limit: 20000m), -15000m, Target("2500.00", state: "Provisional"));
        Assert.Equal("Preliminary", provisional.State); Assert.Contains("ProvisionalRecoveryTarget", provisional.Reasons); Assert.Equal("1500.00", provisional.Amount);
        var incomplete = Margin(Account(Snapshot(1000m), limit: 0m), 1000m, null, "CardCoverageIncomplete");
        Assert.Equal("Preliminary", incomplete.State); Assert.Contains("CardCoverageIncomplete", incomplete.Reasons);
    }

    [Fact]
    public void MarginIsAlwaysForCashSpendAndSaysCardSpendIsNotCovered()
    {
        var margin = Margin(Account(Snapshot(1000m), limit: 0m), 1000m, null);
        Assert.Equal("CashSpend", margin.Scope); Assert.Contains("CardSpendNotCovered", margin.Notes);
        Assert.Contains(margin.Constraints, c => c.Kind == "MinimumReserve" && !c.Applied && c.Reason == "NotConfigured");
    }

    // ---- Meta de recuperação ---------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void FixedOriginalTargetIsPreservedAndOnlyTheGapMoves()
    {
        var weak = FinancialDecisionCalculator.RecoverySummary(true, -15000m, Target("2500.00", projectedRecovery: "1000.00"));
        var strong = FinancialDecisionCalculator.RecoverySummary(true, -15000m, Target("2500.00", projectedRecovery: "3000.00"));
        Assert.Equal("2500.00", weak.Target); Assert.Equal("2500.00", strong.Target);
        Assert.Equal("-1500.00", weak.GapToTarget); Assert.Equal("500.00", strong.GapToTarget);
        Assert.Equal("Fixed", weak.State); Assert.Equal("14000.00", weak.ExpectedClosingUsed);
    }

    [Fact]
    public void RecoveryIsNotApplicableWithoutNegativeOpeningAndUnavailableWithoutBalance()
    {
        Assert.Equal("NotApplicable", FinancialDecisionCalculator.RecoverySummary(true, 10m, null).State);
        Assert.Equal("Unavailable", FinancialDecisionCalculator.RecoverySummary(true, null, null).State);
        Assert.Equal("NotApplicable", FinancialDecisionCalculator.RecoverySummary(false, null, null).State);
        var negativeWithoutRecovery = FinancialDecisionCalculator.RecoverySummary(true, -100m, null);
        Assert.Equal("Unavailable", negativeWithoutRecovery.State); Assert.Null(negativeWithoutRecovery.Target);
    }
}
