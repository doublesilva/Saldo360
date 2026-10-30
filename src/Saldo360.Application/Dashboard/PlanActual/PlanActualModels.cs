namespace Saldo360.Application.Dashboard.PlanActual;

// T10.1: Dashboard Planejado × Projetado × Realizado. Os contratos carregam decimais exatos em texto (duas casas, ponto) e percentuais com uma
// casa; nenhuma fórmula financeira existe no navegador. "NotDefined" é diferente de zero: sem planejamento cadastrado o valor planejado é nulo.

public static class PlanActualScopes
{
    public const string Primary = "primary";
    public const string All = "all";
    public const string Account = "account";
}

public static class PlanActualStates
{
    public const string Complete = "Complete";
    public const string Partial = "Partial";
    public const string Unavailable = "Unavailable";
    public const string Defined = "Defined";
    public const string NotDefined = "NotDefined";
}

public sealed record ScopeAccountDto(Guid AccountId, string Name, string Scope, bool IsPrimary, bool IsActive);

/// <summary>Escopo resolvido. <c>Kind</c> é o escopo efetivo (um pedido "primary" sem conta principal resolve para "all" e marca <c>PrimaryMissing</c>).</summary>
public sealed record PlanActualScopeDto(
    string Requested, string Kind, Guid? AccountId, string? AccountName, string? AccountScope, Guid? PrimaryAccountId, string? PrimaryAccountName, bool PrimaryMissing,
    IReadOnlyList<ScopeAccountDto> Accounts);

public sealed record ComponentDto(string Code, string Amount);

/// <summary>
/// Um indicador. <c>Projected</c> é a melhor estimativa do fechamento do período (realizado + compromissos pendentes); <c>Realized</c> só tem fatos
/// confirmados. As diferenças e percentuais só existem com planejamento definido. <c>State</c>: Complete, Partial (há ressalvas em <c>Reasons</c>).
/// </summary>
public sealed record IndicatorDto(
    string State, string PlannedState, string? Planned, string Projected, string Realized, string Pending, string? RealizedDifference, string? ProjectedDifference,
    string? ExecutionPercent, string? ProjectedPercent, string? PlannedSource, IReadOnlyList<ComponentDto> Components, IReadOnlyList<string> Reasons);

public sealed record ExpensesDto(string PrimaryPerspective, IndicatorDto Consumption, IndicatorDto CashFlow);

public sealed record OverdraftAccountDto(
    Guid AccountId, string Name, string State, string? ContractedLimit, string UsedLatest, DateTimeOffset? ObservedAt, DateOnly? ObservedAsOf,
    string? UsedAtStart, DateOnly? StartAsOf, bool StartApproximate, string? EffectiveRecovery, string? Direction, bool FullyRecovered,
    string? ProjectedUsedAtEnd, string? ProjectedRecovery, string? ProjectedRecoveryFromNow, string? ProjectedBalanceAtEnd, bool ExceedsLimit, IReadOnlyList<string> Reasons);

public sealed record OverdraftDto(
    bool Eligible, string State, string? UsedAtStart, string? UsedLatest, string? EffectiveRecovery, string? ProjectedUsedAtEnd, string? ProjectedRecovery,
    string? ProjectedRecoveryFromNow, string? ContractedLimit, string PeriodKind, IReadOnlyList<OverdraftAccountDto> Accounts, IReadOnlyList<string> Reasons);

public sealed record CardTargetCardDto(Guid CardId, string Name, string? Scope, Guid? PayerAccountId, string Confirmed, string PendingKnown, string Estimated, string Projected);

public sealed record CardTargetDto(
    bool Applicable, string State, string PlannedState, string? Target, string? TargetSource, bool TargetPartial, string Confirmed, string PendingKnown, string Estimated,
    string Projected, string? Remaining, string? ExcessProjected, string? ExcessConfirmed, string? ConfirmedPercent, string? ProjectedPercent, string FinanceCharges,
    IReadOnlyList<CardTargetCardDto> Cards, IReadOnlyList<Guid> PayerAccountIds, IReadOnlyList<string> Reasons);

public sealed record SituationDto(
    string State, string? RealizedBalance, string? PartialRealizedBalance, int ConsideredAccounts, int UnavailableAccounts, string? ProjectedClosing, string? MinimumProjected,
    DateOnly? MinimumProjectedDate, string PendingExpense, string PendingIncome, string CarryoverExpense, string CarryoverIncome, DateOnly? NegativeRiskDate, DateTimeOffset? LatestBankUpdate, IReadOnlyList<string> Reasons);

public sealed record CoverageAccountDto(Guid AccountId, string Name, string? ReconciliationStatus, int ReviewCount, int ReviewInMonth, string? ReferenceReliability, bool BalanceAvailable, bool PartialPeriod);

public sealed record CoverageDto(
    bool Complete, int ReviewCount, int ReviewInMonth, int CardReviewCount, bool ReviewAffectsConfidence, bool PartialPeriod, IReadOnlyList<CoverageAccountDto> Accounts,
    IReadOnlyList<string> Reasons);

public sealed record SyncConnectionDto(Guid ConnectionId, string? Label, string Status, DateTimeOffset? LastSuccessAt, DateTimeOffset? LastAttemptAt, DateTimeOffset? NextAttemptAt, bool ConsentExpiringSoon);

/// <summary>Sincronização das conexões que alimentam o escopo. Nunca resume o escopo todo numa só data: <c>OldestSuccessAt</c> e <c>LatestSuccessAt</c> mostram a diferença.</summary>
public sealed record SyncSummaryDto(
    bool AutoEnabled, int AutoIntervalHours, DateTimeOffset? OldestSuccessAt, DateTimeOffset? LatestSuccessAt, bool SameMoment, bool Stale, string State,
    DateTimeOffset? NextAttemptAt, IReadOnlyList<SyncConnectionDto> Connections);

public sealed record PlanActualDashboardDto(
    string Competence, DateOnly AsOf, string PeriodKind, PlanActualScopeDto Scope, IndicatorDto Income, ExpensesDto Expenses, OverdraftDto Overdraft, CardTargetDto Cards,
    SituationDto Situation, CoverageDto Coverage, SyncSummaryDto? Sync, bool SnapshotConsistent);

public enum PlanActualAccess { Allowed, NotFound, Invalid }

public sealed record PlanActualResult<T>(PlanActualAccess Access, T? Value, IDictionary<string, string[]>? Errors) where T : class
{
    public static PlanActualResult<T> NotFound { get; } = new(PlanActualAccess.NotFound, null, null);
    public static PlanActualResult<T> Ok(T value) => new(PlanActualAccess.Allowed, value, null);
    public static PlanActualResult<T> Invalid(string field, string message) => new(PlanActualAccess.Invalid, null, new Dictionary<string, string[]> { [field] = [message] });
}

