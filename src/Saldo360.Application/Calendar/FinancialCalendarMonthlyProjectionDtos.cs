using Saldo360.Application.Accounts;

namespace Saldo360.Application.Calendar;

public sealed record FinancialCalendarMonthlyProjectionMonth(
    DateOnly Month,
    decimal RealizedIncome,
    decimal RealizedExpense,
    decimal PendingIncome,
    decimal PendingExpense,
    decimal OverdueIncome,
    decimal OverdueExpense,
    decimal? OpeningBalance,
    decimal? ClosingBalance,
    decimal? MinimumProjectedBalance,
    DateOnly? FirstNegativeProjectedDate,
    BalanceUnavailableReason? UnavailableReason);

public sealed record FinancialCalendarMonthlyProjection(
    DateOnly From,
    IReadOnlyList<FinancialCalendarMonthlyProjectionMonth> Months);

public sealed record FinancialCalendarMonthlyProjectionQuery(
    DateOnly From,
    int Months,
    Guid? AccountId);

public sealed record FinancialCalendarMonthlyProjectionResult(
    FinancialCalendarMonthlyProjection? Projection,
    IDictionary<string, string[]>? Errors,
    bool NotFound)
{
    public static FinancialCalendarMonthlyProjectionResult NotFoundResult { get; } = new(null, null, true);
}
