using Saldo360.Domain.Categories;
using Saldo360.Domain.Transactions;

namespace Saldo360.Application.Transactions;

/// <summary>
/// Lançamento como exibido aos integrantes da família. Conta e categoria vêm com nome e situação para que o
/// histórico continue legível depois de desativá-las. Não expõe família nem usuários. PlannedTransactionId indica
/// que o lançamento realiza uma previsão (não editável; cancelar reabre a previsão).
/// </summary>
public sealed record FinancialTransactionDto(
    Guid Id,
    CategoryType Type,
    decimal Amount,
    DateOnly TransactionDate,
    string Description,
    TransactionReferenceDto Account,
    TransactionReferenceDto? Category,
    bool IsCancelled,
    string Currency,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? CancelledAt,
    Guid? PlannedTransactionId,
    FinancialTransactionOrigin Origin);

public sealed record TransactionReferenceDto(Guid Id, string Name, bool IsActive);

/// <summary>Dados enviados pelo cliente (brutos, validados pelo serviço).</summary>
public sealed record TransactionInput(
    Guid? AccountId,
    Guid? CategoryId,
    string? Type,
    string? Description,
    decimal? Amount,
    DateOnly? TransactionDate);

/// <summary>Filtros brutos da listagem (validados pelo serviço).</summary>
public sealed record TransactionQuery(
    DateOnly? From,
    DateOnly? To,
    Guid? AccountId,
    Guid? CategoryId,
    string? Type,
    bool IncludeCancelled,
    int? Page,
    int? PageSize);

/// <summary>Filtros já validados.</summary>
public sealed record TransactionFilter(
    DateOnly? From,
    DateOnly? To,
    Guid? AccountId,
    Guid? CategoryId,
    CategoryType? Type,
    bool IncludeCancelled,
    int Page,
    int PageSize);

/// <summary>Uma página, ordenada por data decrescente e Id decrescente.</summary>
public sealed record TransactionPage(IReadOnlyList<FinancialTransactionDto> Items, int Page, int PageSize, int TotalCount);

/// <summary>Situação de conta ou categoria da família, para validar referências.</summary>
public sealed record AccountState(bool IsActive);

public sealed record CategoryState(CategoryType Type, bool IsActive);

public enum TransactionAccess
{
    Allowed,

    /// <summary>Família ou lançamento inexistente, ou usuário sem participação (404).</summary>
    NotFound,

    /// <summary>Dados inválidos (400).</summary>
    Invalid,

    /// <summary>Estado não permite editar, com mensagem (409): cancelado ou vinculado a previsão.</summary>
    Conflict,
}

public sealed record TransactionResult(
    TransactionAccess Access, FinancialTransactionDto? Transaction, IDictionary<string, string[]>? Errors, string? Message = null)
{
    public const string CancelledMessage = "Lançamento cancelado não pode ser editado.";

    public const string LinkedMessage =
        "Lançamento que realiza uma previsão não pode ser editado. Para corrigir, cancele-o (a previsão volta a ficar pendente) e realize de novo.";

    public static TransactionResult NotFound { get; } = new(TransactionAccess.NotFound, null, null);

    public static TransactionResult Cancelled { get; } = new(TransactionAccess.Conflict, null, null, CancelledMessage);

    public static TransactionResult Linked { get; } = new(TransactionAccess.Conflict, null, null, LinkedMessage);

    public static TransactionResult Ok(FinancialTransactionDto transaction) => new(TransactionAccess.Allowed, transaction, null);

    public static TransactionResult Invalid(IDictionary<string, string[]> errors) => new(TransactionAccess.Invalid, null, errors);
}

public sealed record TransactionListResult(TransactionPage? Page, IDictionary<string, string[]>? Errors, bool NotFound)
{
    public static TransactionListResult NotFoundResult { get; } = new(null, null, true);
}

/// <summary>Existência do lançamento na família e seu vínculo com previsão (null = avulso).</summary>
public sealed record TransactionLink(Guid? PlannedTransactionId);
