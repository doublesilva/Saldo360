using Saldo360.Application.Transactions;
using Saldo360.Domain.Categories;

namespace Saldo360.Application.Planning;

/// <summary>
/// Previsão como exibida aos integrantes da família. Conta e categoria vêm com nome e situação, para o histórico
/// continuar legível depois de desativá-las. Não traz "vencida": isso é derivado da data na exibição.
/// </summary>
public sealed record FinancialPlannedTransactionDto(
    Guid Id,
    CategoryType Type,
    decimal Amount,
    DateOnly DueDate,
    string Description,
    TransactionReferenceDto Account,
    TransactionReferenceDto Category,
    bool IsCancelled,
    string Currency,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? CancelledAt);

/// <summary>Dados enviados pelo cliente (brutos, validados pelo serviço).</summary>
public sealed record PlannedTransactionInput(
    Guid? AccountId,
    Guid? CategoryId,
    string? Type,
    string? Description,
    decimal? Amount,
    DateOnly? DueDate);

/// <summary>Filtros brutos (datas já no formato ISO, validadas na API). O período se aplica a DueDate.</summary>
public sealed record PlannedTransactionQuery(
    DateOnly? From, DateOnly? To, Guid? AccountId, Guid? CategoryId, string? Type, bool IncludeCancelled, int? Page, int? PageSize);

public sealed record PlannedTransactionFilter(
    DateOnly? From, DateOnly? To, Guid? AccountId, Guid? CategoryId, CategoryType? Type, bool IncludeCancelled, int Page, int PageSize);

/// <summary>Uma página, ordenada por data prevista crescente e Id crescente.</summary>
public sealed record PlannedTransactionPage(IReadOnlyList<FinancialPlannedTransactionDto> Items, int Page, int PageSize, int TotalCount);

public enum PlannedTransactionAccess
{
    Allowed,

    /// <summary>Família ou previsão inexistente, ou usuário sem participação (404).</summary>
    NotFound,

    /// <summary>Dados inválidos (400).</summary>
    Invalid,

    /// <summary>Previsão cancelada não pode ser editada (409).</summary>
    Cancelled,
}

public sealed record PlannedTransactionResult(
    PlannedTransactionAccess Access, FinancialPlannedTransactionDto? Planned, IDictionary<string, string[]>? Errors)
{
    public const string CancelledMessage = "Previsão cancelada não pode ser editada.";

    public static PlannedTransactionResult NotFound { get; } = new(PlannedTransactionAccess.NotFound, null, null);

    public static PlannedTransactionResult Cancelled { get; } = new(PlannedTransactionAccess.Cancelled, null, null);

    public static PlannedTransactionResult Ok(FinancialPlannedTransactionDto planned) => new(PlannedTransactionAccess.Allowed, planned, null);

    public static PlannedTransactionResult Invalid(IDictionary<string, string[]> errors) => new(PlannedTransactionAccess.Invalid, null, errors);
}

public sealed record PlannedTransactionListResult(PlannedTransactionPage? Page, IDictionary<string, string[]>? Errors, bool NotFound)
{
    public static PlannedTransactionListResult NotFoundResult { get; } = new(null, null, true);
}
