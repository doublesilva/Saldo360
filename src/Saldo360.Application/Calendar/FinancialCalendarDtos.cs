using Saldo360.Application.Transactions;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Transactions;

namespace Saldo360.Application.Calendar;

/// <summary>Origem do acontecimento exibido na agenda.</summary>
public enum FinancialCalendarSource
{
    Transaction,
    PlannedTransaction,
    DebtInstallment,

    /// <summary>Uma ponta de transferência entre contas próprias (só com includeTransfers=true). Não é receita nem despesa.</summary>
    Transfer,
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
    TransactionReferenceDto? Account,
    TransactionReferenceDto? Category,
    string Currency,
    Guid? PlannedTransactionId,
    FinancialTransactionOrigin Origin = FinancialTransactionOrigin.Manual,
    Guid? RecurrenceId = null,
    int? OccurrenceNumber = null,
    DateOnly? ScheduledDate = null)
{
    // Advisory capabilities only. Existing commands revalidate state under their own locks.
    public IReadOnlyList<string> AllowedActions => Source switch
    {
        FinancialCalendarSource.PlannedTransaction when Status is FinancialCalendarStatus.Pending or FinancialCalendarStatus.Overdue
            => ["Realize", "Edit", "Cancel"],
        FinancialCalendarSource.Transaction when Origin is FinancialTransactionOrigin.Manual or FinancialTransactionOrigin.PlannedRealization
            => PlannedTransactionId is null ? ["Edit", "Cancel"] : ["UndoRealization", "ViewOrigin"],
        _ => [],
    };
}

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
    decimal OverdueExpense)
{
    /// <summary>Devoluções bancárias do período: entradas de caixa que NÃO são receita e que já estão abatidas da despesa realizada (informativo).</summary>
    public decimal RealizedRefunds { get; init; }

    /// <summary>
    /// Transferências do período pela perspectiva da conta (ou do conjunto de todas): recebidas e enviadas são entradas/saídas da conta, SEPARADAS de receitas/despesas;
    /// as internas ao conjunto (sem conta escolhida, origem e destino no orçamento) são neutralizadas e só informadas.
    /// </summary>
    public decimal TransfersReceived { get; init; }
    public decimal TransfersSent { get; init; }
    public decimal TransfersInternal { get; init; }
}

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
    string? Type,
    bool IncludeTransfers = false);

public sealed record FinancialCalendarResult(
    FinancialCalendar? Calendar,
    IDictionary<string, string[]>? Errors,
    bool NotFound)
{
    public static FinancialCalendarResult NotFoundResult { get; } = new(null, null, true);
}
