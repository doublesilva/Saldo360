using Saldo360.Application.Dashboard.Overview;
using Saldo360.Application.Dashboard.PlanActual;

namespace Saldo360.Tests.Application;

/// <summary>T14.3: resultado mensal da Visão Geral. Regras puras de composição: caixa × orçamento, transferências, ponte do saldo e indisponibilidade sem valores fictícios.</summary>
[Trait("Category", "CORE")]
[Trait("Area", "Dashboard")]
public sealed class MonthlyResultCalculatorTests
{
    private static IndicatorDto Indicator(string realized, string pending, params ComponentDto[] components) =>
        PlanActualCalculator.Indicator(null, null, decimal.Parse(realized, System.Globalization.CultureInfo.InvariantCulture), decimal.Parse(pending, System.Globalization.CultureInfo.InvariantCulture), components, []);

    private static PlanActualDashboardDto Plan(string period = "Current", string scope = "all", string income = "5000.00", string incomePending = "1000.00", string cashRealized = "3000.00",
        string cashPending = "1500.00", string consumptionRealized = "4200.00", string consumptionPending = "800.00", string? closing = "3000.00", string pendingIncome = "1000.00", string pendingExpense = "1500.00",
        string carryIncome = "0.00", string carryExpense = "0.00", params ComponentDto[] cashComponents)
    {
        var situation = new SituationDto("Complete", null, null, 1, 0, closing, null, null, pendingExpense, pendingIncome, carryExpense, carryIncome, null, null, []);
        var scopeDto = new PlanActualScopeDto(scope, scope, null, null, null, null, null, false, []);
        return new("2026-11", new(2026, 11, 11), period, scopeDto, Indicator(income, incomePending), new("Consumption", Indicator(consumptionRealized, consumptionPending), Indicator(cashRealized, cashPending, cashComponents)),
            new(false, "NotApplicable", null, null, null, null, null, null, null, period, [], []), null!, situation, new(true, 0, 0, 0, false, false, [], []), null, true);
    }

    private static OverviewTransfersDto Transfers(string received, string sent) => new("Complete", received, sent, "0.00", 1, []);

    private static FinancialDecisionDto Decision(string? bank) => new("Complete",
        new("2026-11", "2026-11", "Current", "primary", null, null, null, new(2026, 11, 11), new(2026, 11, 10), null, null, [], bank, null), null!, null!, null!, null!, true, []);

    [Fact]
    public void CashResultIsInflowsMinusOutflowsAndIsDistinctFromTheBudgetResult()
    {
        // Fatura de R$ 2.000 paga no mês cobre compras de outubro: afeta o caixa; o consumo da competência é outro número.
        var result = MonthlyResultCalculator.Build(Plan(), null, null);

        Assert.Equal(("5000.00", "3000.00", "2000.00"), (result.Inflows.Realized, result.Outflows.Realized, result.Result.Realized));
        Assert.Equal(("6000.00", "4500.00", "1500.00"), (result.Inflows.Projected, result.Outflows.Projected, result.Result.Projected));
        Assert.Equal(("800.00", "1000.00"), (result.Budget.Realized, result.Budget.Projected));
        Assert.NotEqual(result.Result.Realized, result.Budget.Realized);
    }

    [Fact]
    public void InvoicePaymentIsACashOutflowComponentAndPurchasesAreNotASecondOutflow()
    {
        var plan = Plan(cashComponents: [new("BankExpenses", "1000.00"), new("CardInvoicePayments", "2000.00"), new("PendingCardInvoices", "1500.00"), new("PendingBankAmbiguous", "90.00")]);

        var result = MonthlyResultCalculator.Build(plan, null, null);

        var outflows = result.Components.Where(c => c.Side == "Outflow").ToList();
        Assert.Equal(["BankExpenses", "CardInvoicePayments", "PendingCardInvoices"], outflows.Select(c => c.Code));
        Assert.DoesNotContain(result.Components, c => c.Code == "PendingBankAmbiguous"); // não soma duas vezes o que já pode estar na agenda
        Assert.Equal("Cards", outflows.Single(c => c.Code == "PendingCardInvoices").Target);
    }

    [Fact]
    public void InternalTransfersAreNeutralInTheConsolidatedScope()
    {
        var consolidated = MonthlyResultCalculator.Build(Plan(scope: "all"), Transfers("900.00", "900.00"), null);

        Assert.Equal("2000.00", consolidated.Result.Realized);
        Assert.Null(consolidated.TransfersReceived);
        Assert.DoesNotContain(consolidated.Components, c => c.Code.StartsWith("Transfers"));
    }

    [Fact]
    public void TransfersMoveASingleAccountCashButStaySeparateFromIncomeAndExpenses()
    {
        var account = MonthlyResultCalculator.Build(Plan(scope: "account"), Transfers("900.00", "100.00"), null);

        Assert.Equal(("5900.00", "3100.00", "2800.00"), (account.Inflows.Realized, account.Outflows.Realized, account.Result.Realized));
        Assert.Equal(("900.00", "100.00"), (account.TransfersReceived, account.TransfersSent));
        Assert.Equal("5000.00", account.Budget.Income); // a receita orçamentária não inclui transferência
    }

    [Fact]
    public void UnavailableTransfersMakeTheResultPartialInsteadOfAssumingZero()
    {
        var result = MonthlyResultCalculator.Build(Plan(scope: "account"), new OverviewTransfersDto("Unavailable", "0.00", "0.00", "0.00", 0, []), null);

        Assert.Equal("Partial", result.State);
        Assert.Contains("TransfersUnavailable", result.Reasons);
        Assert.Null(result.TransfersReceived);
    }

    [Fact]
    public void FutureMonthShowsOnlyProjectionsWithoutSimulatingRealizedValues()
    {
        var result = MonthlyResultCalculator.Build(Plan(period: "Future", income: "0.00", incomePending: "6000.00", cashRealized: "0.00", cashPending: "4000.00", consumptionRealized: "0.00", consumptionPending: "4500.00"), null, null);

        Assert.Equal((null, null, null), (result.Inflows.Realized, result.Outflows.Realized, result.Result.Realized));
        Assert.Equal(("Projected", "2000.00", "Positive"), (result.Highlight, result.Result.Projected, result.Status));
        Assert.Null(result.Budget.Realized);
    }

    [Fact]
    public void PastMonthHighlightsTheRealizedClosingAndNegativeIsExplicit()
    {
        var result = MonthlyResultCalculator.Build(Plan(period: "Past", income: "3000.00", incomePending: "0.00", cashRealized: "3500.00", cashPending: "0.00"), null, null);

        Assert.Equal(("Realized", "-500.00", "Negative"), (result.Highlight, result.Result.Realized, result.Status));
        Assert.Equal("ClosedMonth", Assert.Single(result.Bridge.Reasons));
    }

    [Fact]
    public void ZeroResultIsNeutral()
    {
        var result = MonthlyResultCalculator.Build(Plan(period: "Past", income: "100.00", incomePending: "0.00", cashRealized: "100.00", cashPending: "0.00"), null, null);

        Assert.Equal("Neutral", result.Status);
    }

    [Fact]
    public void BridgeFromTheObservedBalanceReconcilesWithTheProjectedClosing()
    {
        var plan = Plan(closing: "2000.00", pendingIncome: "1000.00", pendingExpense: "1500.00", carryIncome: "0.00", carryExpense: "500.00");

        var bridge = MonthlyResultCalculator.Build(plan, null, Decision("3000.00")).Bridge;

        Assert.Equal(("Complete", "3000.00", "1000.00", "2000.00", "2000.00"), (bridge.State, bridge.ObservedBalance, bridge.InflowsAfter, bridge.OutflowsAfter, bridge.ProjectedClosing));
        Assert.Null(bridge.Unreconciled);
    }

    [Fact]
    public void BridgeThatDoesNotCloseIsPartialWithTheUnexplainedDifferenceAndNoInventedAdjustment()
    {
        var bridge = MonthlyResultCalculator.Build(Plan(closing: "2800.00"), null, Decision("3000.00")).Bridge;

        Assert.Equal(("Partial", "300.00"), (bridge.State, bridge.Unreconciled)); // 2800 − (3000 + 1000 − 1500)
        Assert.Contains("BridgeNotReconciled", bridge.Reasons);
    }

    [Fact]
    public void MissingBankBalanceOrProjectionNeverInventsABridge()
    {
        var withoutBank = MonthlyResultCalculator.Build(Plan(), null, Decision(null)).Bridge;
        var withoutProjection = MonthlyResultCalculator.Build(Plan(closing: null), null, Decision("3000.00")).Bridge;

        Assert.Equal(("Unavailable", null, "NoBankBalance"), (withoutBank.State, withoutBank.ObservedBalance, withoutBank.Reasons.Single()));
        Assert.Equal(("Partial", null, null), (withoutProjection.State, withoutProjection.InflowsAfter, withoutProjection.Unreconciled));
    }

    private static PlanActualDashboardDto WithOverdraft(OverdraftDto overdraft) => Plan() with { Overdraft = overdraft };

    [Fact]
    public void OverdraftReportsAvailableLimitAndUtilizationFromTheServerAndNeverTreatsItAsOwnBalance()
    {
        var overdraft = new OverdraftDto(true, "Ok", "1000.00", "1000.00", null, "400.00", null, null, "5000.00", "Current", [], []);

        var summary = MonthlyResultCalculator.Build(WithOverdraft(overdraft), null, null).Overdraft!;

        Assert.Equal(("5000.00", "1000.00", "4000.00", "20.0"), (summary.ContractedLimit, summary.Used, summary.Available, summary.UtilizationPercent));
        Assert.Equal(("400.00", "4600.00", "8.0", false), (summary.ProjectedUsed, summary.ProjectedAvailable, summary.ProjectedPercent, summary.ExceedsLimit));
    }

    [Fact]
    public void OverdraftBeyondTheLimitHasZeroAvailableAndNoLimitOrIneligibleAccountHasNoBlock()
    {
        var beyond = MonthlyResultCalculator.Build(WithOverdraft(new OverdraftDto(true, "Ok", "1000.00", "6000.00", null, null, null, null, "5000.00", "Current", [], [])), null, null).Overdraft!;
        var noLimit = MonthlyResultCalculator.Build(WithOverdraft(new OverdraftDto(true, "Ok", "1000.00", "1000.00", null, null, null, null, null, "Current", [], [])), null, null).Overdraft;
        var ineligible = MonthlyResultCalculator.Build(WithOverdraft(new OverdraftDto(false, "NotApplicable", null, null, null, null, null, null, null, "Current", [], [])), null, null).Overdraft;

        Assert.Equal(("0.00", true, null), (beyond.Available, beyond.ExceedsLimit, beyond.ProjectedUsed)); // projeção ausente não vira zero
        Assert.Null(noLimit);
        Assert.Null(ineligible);
    }
}
