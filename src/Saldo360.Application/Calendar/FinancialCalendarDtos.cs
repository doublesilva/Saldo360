using Saldo360.Application.Transactions;
using Saldo360.Domain.Categories;

namespace Saldo360.Application.Calendar;

/// <summary>Origem do acontecimento exibido na agenda.</summary>
public enum FinancialCalendarSource
{
    Transaction,
    PlannedTransaction,
}

/// <summary>
/// Situação do acontecimento. Lançamentos são realizados; previsões sem lançamento ativo são pendentes ou vencidas.
/// </summary>
public enum FinancialCalendarStatus
{
    Realized,
    Pending,
    Overdue,
}

/// <summary>
/// Um acontecimento financeiro. Date e Amount são sempre os dados do item exibido: efetivos para lançamentos e
/// previstos para previsões pendentes. PlannedTransactionId permite navegar da realização para a previsão original.
/// </summary>
public sealed record FinancialCalendarItemDto(
    Guid Id,
    FinancialCalendarSource Source,
    FinancialCalendarStatus Status,
    CategoryType Type,
    decimal Amount,
    DateOnly Date,
    string Description,
    TransactionReferenceDto Account,
    TransactionReferenceDto Category,
    string Currency,
    Guid? PlannedTransactionId);

public sealed record FinancialCalendarFilter(
    DateOnly From,
    DateOnly Through,
    Guid? AccountId,
    CategoryType? Type,
    DateOnly Today);

public sealed record FinancialCalendarSummaryDto(
    decimal RealizedIncome,
    decimal RealizedExpense,
    decimal PendingIncome,
    decimal PendingExpense,
    decimal OverdueIncome,
    decimal OverdueExpense);

public sealed record FinancialCalendarSnapshot(
    IReadOnlyList<FinancialCalendarItemDto> Items,
    FinancialCalendarSummaryDto Summary);

public sealed record FinancialCalendar(
    DateOnly From,
    DateOnly Through,
    IReadOnlyList<FinancialCalendarItemDto> Items,
    FinancialCalendarSummaryDto Summary);

public sealed record FinancialCalendarQuery(
    DateOnly From,
    DateOnly Through,
    Guid? AccountId,
    string? Type);

public sealed record FinancialCalendarResult(
    FinancialCalendar? Calendar,
    IDictionary<string, string[]>? Errors,
    bool NotFound)
{
    public static FinancialCalendarResult NotFoundResult { get; } = new(null, null, true);
}
