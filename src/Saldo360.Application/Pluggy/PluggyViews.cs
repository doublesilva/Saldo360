using Saldo360.Domain.Pluggy;

namespace Saldo360.Application.Pluggy;

public interface IPluggyItemLinkRepository
{
    Task<IReadOnlyList<PluggyItemLink>> ListAsync(Guid workspaceId, CancellationToken ct);
    Task<PluggyItemLink?> FindAsync(Guid workspaceId, Guid id, CancellationToken ct);
    /// <summary>Falso quando o Item já está vinculado (a qualquer orçamento): um Item pertence a um único vínculo.</summary>
    Task<bool> TryAddAsync(PluggyItemLink link, CancellationToken ct);
}

public enum PluggyAccess { Allowed, NotFound, Invalid, Conflict, IntegrationFailure }

public sealed record PluggyResult<T>(PluggyAccess Access, T? Value = default, PluggyFailureKind? Failure = null, string? Message = null)
{
    public static PluggyResult<T> Ok(T value) => new(PluggyAccess.Allowed, value);
    public static PluggyResult<T> NotFound() => new(PluggyAccess.NotFound);
    public static PluggyResult<T> Invalid(string message) => new(PluggyAccess.Invalid, Message: message);
}

public enum PluggyConnectionState { Healthy, Updating, ActionRequired, ConsentExpired, Error, Unknown }

public sealed record PluggyConnectionView(Guid Id, string? Label, string ItemReference, string? Institution, string? InstitutionType, bool? IsOpenFinance,
    PluggyConnectionState State, string? Status, string? ExecutionStatus, DateTimeOffset? LastUpdatedAt, DateTimeOffset? ConsentExpiresAt,
    string? ErrorCode, string? ErrorMessage, string? IntegrationError, DateTimeOffset CreatedAt);

public sealed record PluggyAccountView(string Id, Guid ConnectionId, string? Type, string? Subtype, string? Name, string? MarketingName,
    string? MaskedNumber, string? MaskedBranchAccount, string? Currency, string? Balance, DateTimeOffset? UpdatedAt,
    string? OverdraftContractedLimit, string? OverdraftUsedLimit, string? CreditLimit, string? AvailableCreditLimit, string? MinimumPayment,
    string? CardBrand, string? CardLevel, DateTimeOffset? BalanceCloseDate, DateTimeOffset? BalanceDueDate);

public sealed record PluggyTransactionView(string Id, DateTimeOffset? Date, string? Description, string? DescriptionRaw, string? Amount,
    string? AmountInAccountCurrency, string? Currency, string? Type, string? Status, string? Category, string? Balance, string? OperationType,
    string? PaymentMethod, bool HasPayer, bool HasReceiver, bool HasReferenceNumber, string? Merchant, string? MerchantCategory,
    int? InstallmentNumber, int? TotalInstallments, string? InstallmentsTotalAmount, string? BillId, string? CardLast4);

public sealed record PluggyTransactionsView(IReadOnlyList<PluggyTransactionView> Items, string? NextCursor);

public sealed record PluggyFinanceChargeView(string? Type, string? Amount, string? Currency, string? AdditionalInfo);
public sealed record PluggyBillPaymentView(string? Amount, string? Currency, DateTimeOffset? PaymentDate, string? PaymentMode, string? ValueType);
public sealed record PluggyBillView(string Id, DateTimeOffset? DueDate, DateTimeOffset? ClosingDate, string? TotalAmount, string? Currency, string? MinimumPayment,
    bool? AllowsInstallments, IReadOnlyList<PluggyFinanceChargeView> FinanceCharges, IReadOnlyList<PluggyBillPaymentView> Payments);
public sealed record PluggyBillsView(IReadOnlyList<PluggyBillView> Items, int Page, int? TotalPages, int? Total);

// ---- Diagnóstico de cobertura -------------------------------------------------------------------------------------

public enum PluggyResourceState
{
    /// <summary>Consulta concluída e com registros.</summary>
    Available,
    /// <summary>Consulta concluída sem registros: ausência de dados, não falha.</summary>
    Empty,
    /// <summary>O produto não se aplica a este tipo de conta ou conector.</summary>
    NotSupported,
    /// <summary>A Pluggy informou que o recurso não está disponível (acesso negado ou inexistente).</summary>
    Unavailable,
    /// <summary>Falha de integração (timeout, limite de requisições, indisponibilidade, resposta inválida).</summary>
    Error,
    /// <summary>Autorização do Item expirada ou pendente de ação do usuário.</summary>
    ConsentExpired,
}

public sealed record PluggyFieldCoverage(string Field, int Present, int Total);

public sealed record PluggyResourceCoverage(string Resource, PluggyResourceState State, int Count, DateTimeOffset? EarliestDate, DateTimeOffset? LatestDate,
    IReadOnlyList<PluggyFieldCoverage> Fields, IReadOnlyList<string> Limitations, string? ErrorKind)
{
    public IReadOnlyList<string> FieldsReturned => Fields.Where(f => f.Present > 0).Select(f => f.Field).ToArray();
    public IReadOnlyList<string> FieldsMissing => Fields.Where(f => f.Present == 0).Select(f => f.Field).ToArray();
}

public sealed record PluggyAccountCoverage(string AccountId, string? Name, string? Type, string? Subtype, DateTimeOffset? UpdatedAt,
    IReadOnlyList<PluggyResourceCoverage> Resources);

public sealed record PluggyDiagnosticsView(Guid ConnectionId, string? Institution, PluggyConnectionState State, DateTimeOffset? LastSync,
    DateTimeOffset GeneratedAt, IReadOnlyList<string> ConnectorProducts, IReadOnlyList<PluggyResourceCoverage> ItemResources,
    IReadOnlyList<PluggyAccountCoverage> Accounts, IReadOnlyList<string> Notes);
