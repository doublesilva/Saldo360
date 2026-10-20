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
    BalanceUnavailableReason? UnavailableReason,
    bool CreditCardProjectionComplete = true,
    decimal? CreditCardOutstanding = null,
    decimal CardPayments = 0m,
    decimal? BudgetTarget = null,
    decimal? BudgetConsumption = null,
    string BudgetStatus = "NotDefined",
    string PeriodKind = "Future",
    bool DebtProjectionComplete = true)
{
    public decimal TotalIncome => RealizedIncome + PendingIncome;
    public decimal TotalExpense => RealizedExpense + PendingExpense;
    public decimal? FixedExpenses => null; // Recurrence is not a reliable fixed-expense classification.
    public string Status => ClosingBalance is null ? "Unavailable" : FirstNegativeProjectedDate is not null || ClosingBalance < 0m
        ? "NegativeProjectedBalance" : BudgetStatus == "Exceeded" ? "AboveTarget" : "WithinKnownCommitments";
}

public sealed record FinancialCalendarMonthlyProjection(
    DateOnly From,
    IReadOnlyList<FinancialCalendarMonthlyProjectionMonth> Months)
{
    public DateOnly Through => Months[^1].Month;
    public decimal TotalIncome => Months.Sum(x => x.TotalIncome);
    public decimal TotalExpense => Months.Sum(x => x.TotalExpense);
    public decimal? LowestClosingBalance => Months.All(x => x.ClosingBalance is not null) ? Months.Min(x => x.ClosingBalance) : null;
    public DateOnly? LowestBalanceMonth => LowestClosingBalance is { } value ? Months.First(x => x.ClosingBalance == value).Month : null;
    public int MonthsOverBudget => Months.Count(x => x.BudgetStatus == "Exceeded");
    public int MonthsWithNegativeBalance => Months.Count(x => x.FirstNegativeProjectedDate is not null || x.ClosingBalance < 0m);
}

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
