using Saldo360.Application.Calendar;

namespace Saldo360.Application.Dashboard;

public sealed record FinancialDashboard(
    DateOnly Competence,
    int Months,
    FinancialDashboardPosition Position,
    FinancialDashboardCashFlow CashFlow,
    FinancialDashboardObligations Obligations,
    FinancialDashboardBudgets Budgets,
    FinancialDashboardCreditCards CreditCards,
    FinancialDashboardDebts Debts,
    FinancialDashboardGoals Goals,
    IReadOnlyList<FinancialDashboardMonthlyProjection> MonthlyProjection,
    FinancialDashboardCoverage Coverage,
    FinancialDashboardIndicators Indicators,
    IReadOnlyList<FinancialDashboardAlert> Alerts,
    DateOnly AsOf);

public sealed record FinancialDashboardPosition(decimal? RealizedBalance, IReadOnlyList<FinancialDashboardAccountBalance> Accounts, int ConsideredAccounts, decimal NegativeBalance, decimal AllocatedToGoals, decimal? UnallocatedRealizedBalance);
public sealed record FinancialDashboardAccountBalance(Guid AccountId, string Name, decimal? RealizedBalance, bool Complete);
public sealed record FinancialDashboardCashFlow(decimal RealizedIncome, decimal RealizedExpense, decimal PendingIncome, decimal PendingExpense);
public sealed record FinancialDashboardObligations(decimal PendingIncome, decimal PendingExpense, decimal CreditCardOutstanding, decimal DebtOutstanding, int PendingItems);
public sealed record FinancialDashboardBudgets(int TargetCount, decimal Planned, decimal Committed, decimal Realized, int ExceededCount, int AttentionCount);
public sealed record FinancialDashboardCreditCards(int Count, decimal Outstanding, int OverdueInvoices, int FutureInvoices, bool HasUnallocatedObligations, decimal? AvailableLimit);
public sealed record FinancialDashboardDebts(int ActiveContracts, int OverdueInstallments, decimal InstallmentsDueInHorizon, decimal? DeclaredOutstandingBalance, int UnallocatedObligations);
public sealed record FinancialDashboardGoals(int Active, int Completed, int Paused, decimal Allocated, decimal Unallocated, bool CoverageComplete);
public sealed record FinancialDashboardMonthlyProjection(DateOnly Competence, decimal? ClosingBalance, decimal RealizedIncome, decimal RealizedExpense, decimal PendingIncome, decimal PendingExpense, decimal CreditCardOutstanding, bool ProjectionComplete);
public sealed record FinancialDashboardCoverage(bool Complete, bool HasUnallocatedCreditCardObligations, bool HasUnallocatedDebtObligations, string? Reason);
public enum FinancialDashboardTrend { Improving, Stable, Deteriorating, Unknown }
public sealed record FinancialDashboardIndicators(decimal? RealizedBalance, decimal? ProjectedBalance, decimal? ProjectedVariation, string? ProjectedVariationReference, decimal RealizedNet, decimal PendingNet, decimal KnownNet, decimal Commitment, decimal? UnallocatedAvailability, FinancialDashboardTrend Trend, FinancialDashboardNextCommitment? NextCommitment, FinancialDashboardCapacityBeforeIncome CapacityBeforeNextIncome);
public sealed record FinancialDashboardNextCommitment(FinancialCalendarSource Source, Guid Id, DateOnly Date, decimal Amount, string Description, Guid? AccountId);
public sealed record FinancialDashboardCapacityBeforeIncome(string Status, DateOnly? NextIncomeDate, decimal? NextIncomeAmount, decimal? ProjectedBalanceBeforeIncome, decimal? MinimumProjectedBalance);
public enum FinancialDashboardAlertSeverity { Info, Warning, Critical }
public sealed record FinancialDashboardAlert(string Code, FinancialDashboardAlertSeverity Severity, string Title, string Description, DateOnly? Competence, decimal? Amount, Guid? SourceId, string? Context, DateOnly ReferenceDate);

public sealed record FinancialDashboardResult(FinancialDashboard? Dashboard, IDictionary<string, string[]>? Errors, bool NotFound)
{
    public static FinancialDashboardResult NotFoundResult { get; } = new(null, null, true);
}
