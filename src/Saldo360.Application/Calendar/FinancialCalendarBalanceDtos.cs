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
    IReadOnlyList<FinancialCalendarDailyTotals>? PeriodRealized = null,
    IReadOnlyList<FinancialCalendarDailyTotals>? CreditCardObligations = null,
    bool HasUnallocatedCreditCardObligations = false,
    IReadOnlyList<FinancialCalendarDailyTotals>? DebtObligations = null,
    bool HasUnallocatedDebtObligations = false,
    IReadOnlyList<FinancialCalendarDailyTotals>? CardPayments = null,
    IReadOnlyList<Saldo360.Application.Budgets.FinancialBudgetExecutionSource>? Budgets = null,
    IReadOnlyList<PendingBankClassification>? PendingBank = null,
    IReadOnlyList<FinancialCalendarDailyTotals>? InferredPending = null,
    bool HasCardObligationsWithoutPayer = false,
    bool HasDebtObligationsWithoutResponsible = false)
{
    /// <summary>
    /// O mesmo snapshot sem as previsões INFERIDAS (receita PJ estimada por horas e repasse PJ → PF projetado a partir dela), que já estão somadas em <c>Pending</c>.
    /// Serve à análise conservadora (T11.1): entrada inferida nunca é entrada garantida. Não altera nenhum dado lido.
    /// </summary>
    public FinancialCalendarBalanceSnapshot WithoutInferred() => InferredPending is not { Count: > 0 } inferred ? this : this with
    {
        Pending = (Pending ?? []).Concat(inferred.Select(row => new FinancialCalendarDailyTotals(row.Date, -row.Income, -row.Expense, -row.TransferIn, -row.TransferOut))).ToList(),
        InferredPending = [],
    };
}

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
    FinancialCalendarBalanceRisk Risk,
    bool CreditCardProjectionComplete = true,
    decimal? CreditCardOutstanding = null,
    bool DebtProjectionComplete = true,
    decimal? DebtInstallmentOutstanding = null);

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
