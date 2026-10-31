using Saldo360.Domain.Pluggy;

namespace Saldo360.Application.Pluggy;

public sealed record PluggyImportedTransactionView(Guid Id, Guid ConnectionId, string AccountId, string? AccountType, string? AccountLabel,
    string PluggyTransactionId, DateTimeOffset? Date, string? Description, string? DescriptionRaw, string? Amount, string? Currency, string? Type,
    string Status, string? OperationType, string? Category, int? InstallmentNumber, int? TotalInstallments, string? BillId, string? CardLast4,
    string Classification, string ClassificationReason, DateTimeOffset FirstImportedAt, DateTimeOffset LastObservedAt, DateTimeOffset? LastChangedAt,
    int MissingSyncCount, string? PossibleDuplicateKey, int PossibleDuplicateCount, bool IsNewInLastSync, bool ChangedInLastSync,
    string FinancialStatus = PluggyFinancialStatuses.NotIntegrated, string? FinancialReason = null, Guid? PostingId = null);

/// <summary>
/// Situação financeira de um movimento importado, calculada no servidor a partir do posting (evidência importada x efeito aplicado):
/// Accounted (lançamento de conta), Transfer, BillPayment, CardPurchase, CardCredit, Refund, Evidence (ponta de outro registro), Waiting (pendente/futuro),
/// InReview (motivo em FinancialReason; decisão na Revisão), Ignored, OutOfRange (anterior ao início da importação), NotProcessed (ainda sem posting),
/// NotIntegrated (conta da Pluggy sem vínculo com o Saldo360).
/// </summary>
public static class PluggyFinancialStatuses
{
    public const string Accounted = "Accounted", Transfer = "Transfer", BillPayment = "BillPayment", CardPurchase = "CardPurchase", CardCredit = "CardCredit", Refund = "Refund",
        Evidence = "Evidence", Waiting = "Waiting", InReview = "InReview", Ignored = "Ignored", OutOfRange = "OutOfRange", NotProcessed = "NotProcessed", NotIntegrated = "NotIntegrated";

    public static (string Status, string? Reason) Of(PluggyPostingState? state, PluggyPostingKind kind, string? reason, bool linked, DateOnly? importFrom, DateOnly? date)
    {
        if (!linked) return (NotIntegrated, null);
        if (state is null) return importFrom is { } from && date is { } d && d < from ? (OutOfRange, null) : (NotProcessed, null);
        return state switch
        {
            PluggyPostingState.WaitingPosted => (Waiting, reason),
            PluggyPostingState.NeedsReview => (InReview, reason),
            PluggyPostingState.Ignored => (Ignored, reason),
            PluggyPostingState.Covered => (Evidence, null),
            _ => kind switch
            {
                PluggyPostingKind.Transfer => (Transfer, null), PluggyPostingKind.CardBillPayment => (BillPayment, null), PluggyPostingKind.CardPurchase => (CardPurchase, null),
                PluggyPostingKind.CardCredit => (CardCredit, null), PluggyPostingKind.BankRefund => (Refund, null), _ => (Accounted, null),
            },
        };
    }
}

public sealed record PluggyImportCounters(int Total, int New, int Updated, int Pending, int Missing, DateTimeOffset? LastSyncAt);

public sealed record PluggyImportedPage(IReadOnlyList<PluggyImportedTransactionView> Items, int Total, int Page, int PageSize, PluggyImportCounters Counters);

public sealed record PluggySyncRunView(Guid Id, Guid ConnectionId, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string Status, int AccountsTotal,
    int AccountsSynced, int Seen, int Created, int Updated, int Unchanged, int Missing, int Errors, string? ErrorSummary, string Trigger = "Manual");

public sealed record PluggyImportFilter(Guid? ConnectionId, string? AccountId, DateOnly? From, DateOnly? To, PluggyTransactionStatus? Status,
    PluggyTransactionClass? Classification, int Page, int PageSize);

/// <summary>Evidências de estabilidade entre as duas últimas coletas concluídas e acumuladas de todas.</summary>
public sealed record PluggyStabilityView(Guid ConnectionId, int CompletedRuns, Guid? PreviousRunId, Guid? LatestRunId, DateTimeOffset? PreviousRunAt,
    DateTimeOffset? LatestRunAt, int PreviouslyImported, int PreservedInLatest, int NotSeenInLatest, int NewInLatest, int NewWithDateBeforePreviousRun,
    IReadOnlyDictionary<string, int> ChangedFieldsInLatest, int PendingToPostedInLatest, int OtherStatusChangesInLatest,
    int PendingToPostedTotal, int AmountChangesTotal, int DescriptionChangesTotal, int FutureDated, int FutureDatedPending, int MissingNow,
    int PendingNow, int PossibleDuplicateGroups, IReadOnlyList<string> Notes);

public sealed class PluggyAccountApply
{
    public int Seen { get; set; }
    public int New { get; set; }
    public int Updated { get; set; }
    public int Unchanged { get; set; }
    public int Missing { get; set; }
    public int DuplicatePayloadIds { get; set; }
}

/// <summary>Persistência da importação. Nunca toca tabelas financeiras operacionais.</summary>
public interface IPluggyImportRepository
{
    /// <summary>Inicia uma coleta, ou devolve nulo se a conexão já tem uma em andamento (coletas abandonadas são encerradas antes).</summary>
    Task<PluggySyncRun?> TryStartRunAsync(Guid workspaceId, Guid connectionId, Guid userId, DateTimeOffset now, TimeSpan staleAfter, PluggySyncTrigger trigger, CancellationToken ct);
    Task SaveRunAsync(PluggySyncRun run, CancellationToken ct);
    /// <summary>
    /// Aplica a coleta de uma conta de forma idempotente (inserir/atualizar por conexão+conta+ID externo). Se <paramref name="complete"/>,
    /// as transações conhecidas da conta que não vieram apenas acumulam ausência; nada é excluído.
    /// </summary>
    Task<PluggyAccountApply> ApplyAccountAsync(Guid workspaceId, Guid connectionId, string accountId, IReadOnlyList<PluggyTransactionSnapshot> snapshots,
        bool complete, Guid runId, DateTimeOffset now, CancellationToken ct);
    Task<PluggyImportedPage> ListAsync(Guid workspaceId, PluggyImportFilter filter, DateTimeOffset now, CancellationToken ct);
    Task<IReadOnlyList<PluggySyncRun>> ListRunsAsync(Guid workspaceId, Guid connectionId, int take, CancellationToken ct);
    Task<PluggyStabilityView> GetStabilityAsync(Guid workspaceId, Guid connectionId, DateTimeOffset now, CancellationToken ct);
}
