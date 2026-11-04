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

    /// <summary>Ocorrência virtual de uma recorrência (calculada, não gravada). Sem ações de edição/realização.</summary>
    RecurrenceOccurrence,
}

/// <summary>
/// Situação do acontecimento. Lançamentos são realizados; previsões sem lançamento ativo são pendentes ou vencidas.
/// </summary>
public enum FinancialCalendarStatus
{
    Realized,
    Pending,
    Overdue,

    /// <summary>Despesa fixa de competência anterior à implantação, sem evidência de pagamento: nem dívida confirmada nem paga.</summary>
    NeedsReview,
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
    DateOnly? ScheduledDate = null,
    Guid? PlannedTransferId = null,
    string? ReconciliationStatus = null,
    decimal? ActualAmount = null,
    DateOnly? ActualDate = null,
    Guid? TransactionId = null,
    decimal? BaseAmount = null,
    string? AmountSource = null,
    string? ConfirmationSource = null)
{
    // Advisory capabilities only. Existing commands revalidate state under their own locks.
    public IReadOnlyList<string> AllowedActions => Source switch
    {
        FinancialCalendarSource.PlannedTransaction when Status is FinancialCalendarStatus.Pending or FinancialCalendarStatus.Overdue
            => ["Realize", "Edit", "Cancel"],
        FinancialCalendarSource.Transaction when Origin is FinancialTransactionOrigin.Manual or FinancialTransactionOrigin.PlannedRealization
            => PlannedTransactionId is null ? ["Edit", "Cancel"] : ["UndoRealization", "ViewOrigin"],
        FinancialCalendarSource.Transfer when PlannedTransferId is not null && Status is FinancialCalendarStatus.Pending or FinancialCalendarStatus.Overdue => ["CancelPlannedTransfer"],
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

    /// <summary>Transferências PREVISTAS pendentes do período (ainda não realizadas), separadas de receitas e despesas previstas.</summary>
    public decimal PlannedTransfersReceived { get; init; }
    public decimal PlannedTransfersSent { get; init; }

    /// <summary>Total de entradas/saídas de caixa REALIZADAS do período: receitas (ou despesas brutas) + devoluções + transferências que cruzam o conjunto. Sem previstos.</summary>
    public decimal TotalInflows { get; init; }
    public decimal TotalOutflows { get; init; }
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
