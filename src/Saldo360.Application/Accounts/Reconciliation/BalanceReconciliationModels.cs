namespace Saldo360.Application.Accounts.Reconciliation;

// T09.5: saldo bancário × saldo calculado das contas integradas. Tudo é leitura: nenhuma diferença vira ajuste, lançamento ou saldo inicial.

/// <summary>Classificação da conta. Exatamente uma por conta; a ordem de decisão está em <see cref="BalanceReconciliation.Classify"/>.</summary>
public enum ReconciliationStatus { NoDivergence, DivergenceFound, IncompleteHistory, MovementsInReview, InsufficientBankReference }

/// <summary>Observação de saldo do banco (âncora). A natureza (contábil/disponível/desconhecida) é a informada, nunca presumida.</summary>
public sealed record AnchorFact(
    DateTimeOffset ObservedAt, DateOnly AsOfDate, decimal Amount, string Kind, string Source, string Reliability, string? Reason, bool IsCurrent,
    decimal? DerivedOpeningAmount, DateOnly? DerivedOpeningDate);

/// <summary>
/// Movimentação efetivada (POSTED) da conta, como o banco a informou. <paramref name="SignedAmount"/> é nulo quando não dá para assinar com segurança
/// (sem tipo, valor, data ou em outra moeda): ela interrompe a reconstrução bancária até a sua data. <paramref name="State"/> é o estado no Saldo360
/// (Applied, NeedsReview, Ignored, Waiting...).
/// </summary>
public sealed record BankMovementFact(DateOnly Date, decimal? SignedAmount, string State, string? Reason)
{
    public bool InReview => State == "NeedsReview" || (State == "Ignored" && Reason != "BeforeImportFrom");
}

public sealed record IntegratedAccountFacts(
    Guid AccountId, string Name, string Scope, bool IsActive, string LinkStatus, DateTimeOffset? LastSyncedAt, DateOnly? ImportFrom,
    decimal? OverdraftContractedLimit, decimal? OverdraftUsedLimit, string? PluggySubtype,
    IReadOnlyList<AnchorFact> Anchors, IReadOnlyList<BankMovementFact> Posted, int PendingCount);

public interface IAccountBalanceReconciliationRepository
{
    Task<IReadOnlyList<IntegratedAccountFacts>> LoadAsync(Guid workspaceId, Guid? accountId, CancellationToken cancellationToken);
}

// ---- Respostas ---------------------------------------------------------------------------------------------------------------

public sealed record OverdraftDto(string? ContractedLimit, string? UsedLimit, string Note);
public sealed record BankReferenceDto(string Amount, string Kind, DateTimeOffset ObservedAt, DateOnly AsOfDate, string Source, string Reliability, string? ReliabilityReason, OverdraftDto? Overdraft);
public sealed record CalculatedBalanceDto(string? Amount, DateOnly AsOf, string OpeningSource, DateOnly? CalculableFrom, string? UnavailableReason);
public sealed record HistoryCoverageDto(DateOnly? ImportedFrom, DateOnly? ImportedThrough, DateOnly? BankSideFrom, DateOnly? LedgerFrom, string Quality, int PostedMovements, string? Reason);
public sealed record ReviewSummaryDto(int Count, string NetAmount, DateOnly? FirstDate, DateOnly? LastDate, int BeforeLedgerStart, int InsideLedgerRange, int Pending);
public sealed record ConsistencyDto(int Checked, int Matching, string Note);

public sealed record AccountReconciliationDto(
    Guid AccountId, string Name, string Scope, bool IsActive, BankReferenceDto? Bank, CalculatedBalanceDto Calculated, string? Difference, string Status, string? StatusReason,
    string? ExplainedByReview, string? Unexplained, DateTimeOffset? LastSyncedAt, string LinkStatus, HistoryCoverageDto Coverage, ReviewSummaryDto Review, ConsistencyDto Consistency);

public sealed record ScopeSummaryDto(string Scope, int Accounts, string? Bank, string? Calculated, string? Difference, bool Complete, IReadOnlyList<string> Reasons);
public sealed record ReconciliationOverviewDto(DateOnly Today, IReadOnlyList<AccountReconciliationDto> Accounts, IReadOnlyList<ScopeSummaryDto> ByScope, ScopeSummaryDto Consolidated);

/// <summary><c>BlockingReview</c>: movimentações em revisão datadas do início do mês em diante; enquanto existirem, a abertura calculada do mês não existe (o saldo observado só alcança a data seguinte à última delas).</summary>
public sealed record MonthBalanceDto(string Month, string? LedgerOpening, string? LedgerReason, string? BankOpening, string? BankReason, string? Difference, int ReviewInMonth, string ReviewNetInMonth, int BlockingReview = 0);
public sealed record ObservationCheckDto(DateOnly AsOf, DateTimeOffset ObservedAt, string Observed, string? Ledger, string? LedgerDifference, string? BankSide, string? BankSideDifference, string Status);
public sealed record AccountReconciliationPeriodDto(Guid AccountId, string Name, string Scope, string From, string Through, IReadOnlyList<MonthBalanceDto> Months, IReadOnlyList<ObservationCheckDto> Observations);

public enum ReconciliationAccess { Allowed, NotFound, Invalid }
public sealed record ReconciliationResult<T>(ReconciliationAccess Access, T? Value, IDictionary<string, string[]>? Errors) where T : class
{
    public static ReconciliationResult<T> NotFound { get; } = new(ReconciliationAccess.NotFound, null, null);
    public static ReconciliationResult<T> Ok(T value) => new(ReconciliationAccess.Allowed, value, null);
    public static ReconciliationResult<T> Invalid(string field, string message) => new(ReconciliationAccess.Invalid, null, new Dictionary<string, string[]> { [field] = [message] });
}
