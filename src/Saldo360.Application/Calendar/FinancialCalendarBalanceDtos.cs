using Saldo360.Application.Accounts;

namespace Saldo360.Application.Calendar;

public enum FinancialCalendarBalanceKind
{
    Realized,
    Projected,
}

public sealed record FinancialCalendarBalanceAccount(
    Guid Id,
    decimal? OpeningBalance,
    DateOnly? OpeningBalanceDate);

public sealed record FinancialCalendarDailyTotals(
    DateOnly Date,
    decimal Income,
    decimal Expense,
    decimal TransferIn,
    decimal TransferOut);

public sealed record FinancialCalendarBalanceSnapshot(
    IReadOnlyList<FinancialCalendarBalanceAccount> Accounts,
    IReadOnlyList<FinancialCalendarDailyTotals> Realized,
    IReadOnlyList<FinancialCalendarDailyTotals> Pending,
    IReadOnlyList<FinancialCalendarDailyTotals>? PeriodRealized = null);

public sealed record FinancialCalendarBalanceDay(
    DateOnly Date,
    FinancialCalendarBalanceKind Kind,
    decimal? Income,
    decimal? Expense,
    decimal? TransferIn,
    decimal? TransferOut,
    decimal? ClosingBalance,
    bool HasOverduePending,
    bool IsNegative,
    int? ChartPosition,
    BalanceUnavailableReason? UnavailableReason);

public sealed record FinancialCalendarBalanceRisk(
    decimal? MinimumProjectedBalance,
    DateOnly? FirstNegativeProjectedDate,
    decimal? EndingProjectedBalance);

public sealed record FinancialCalendarBalances(
    DateOnly From,
    DateOnly Through,
    Guid? AccountId,
    decimal? OpeningBalance,
    BalanceUnavailableReason? UnavailableReason,
    IReadOnlyList<FinancialCalendarBalanceDay> Days,
    FinancialCalendarBalanceRisk Risk);

public sealed record FinancialCalendarBalanceQuery(
    DateOnly From,
    DateOnly Through,
    Guid? AccountId);

public sealed record FinancialCalendarBalanceResult(
    FinancialCalendarBalances? Balances,
    IDictionary<string, string[]>? Errors,
    bool NotFound)
{
    public static FinancialCalendarBalanceResult NotFoundResult { get; } = new(null, null, true);
}
