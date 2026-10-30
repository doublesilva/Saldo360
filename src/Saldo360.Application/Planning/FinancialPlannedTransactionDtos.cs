using Saldo360.Application.Transactions;
using Saldo360.Domain.Categories;

namespace Saldo360.Application.Planning;

/// <summary>
/// Situação da previsão, sempre derivada (nunca gravada): <see cref="Cancelled"/> se IsCancelled; senão
/// <see cref="Realized"/> se existe lançamento vinculado NÃO cancelado; senão <see cref="Pending"/>.
/// </summary>
public enum PlannedTransactionStatus
{
    Pending,
    Realized,
    Cancelled,
}

/// <summary>Lançamento efetivo ativo que realiza a previsão: valor e data efetivos, separados dos previstos.</summary>
public sealed record PlannedRealizationDto(Guid TransactionId, decimal Amount, DateOnly TransactionDate);

/// <summary>
/// Previsão como exibida aos integrantes da família. Amount e DueDate são sempre os PREVISTOS; a realização,
/// quando houver, vem em <see cref="Realization"/>. Não traz "vencida": isso é derivado da data na exibição.
/// </summary>
public sealed record FinancialPlannedTransactionDto(
    Guid Id,
    CategoryType Type,
    decimal Amount,
    DateOnly DueDate,
    string Description,
    TransactionReferenceDto Account,
    TransactionReferenceDto Category,
    PlannedTransactionStatus Status,
    PlannedRealizationDto? Realization,
    bool IsCancelled,
    string Currency,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? CancelledAt,
    ExpenseNature Nature = ExpenseNature.Undefined);

/// <summary>Dados enviados pelo cliente (brutos, validados pelo serviço).</summary>
public sealed record PlannedTransactionInput(
    Guid? AccountId,
    Guid? CategoryId,
    string? Type,
    string? Description,
    decimal? Amount,
    DateOnly? DueDate,
    string? ExpenseNature = null);

/// <summary>
/// Filtros brutos (datas já no formato ISO, validadas na API). O período se aplica a DueDate. Por padrão só
/// pendentes; IncludeRealized e IncludeCancelled acrescentam as realizadas e as canceladas.
/// </summary>
public sealed record PlannedTransactionQuery(
    DateOnly? From, DateOnly? To, Guid? AccountId, Guid? CategoryId, string? Type,
    bool IncludeRealized, bool IncludeCancelled, int? Page, int? PageSize, string? ExpenseNature = null);

public sealed record PlannedTransactionFilter(
    DateOnly? From, DateOnly? To, Guid? AccountId, Guid? CategoryId, CategoryType? Type,
    bool IncludeRealized, bool IncludeCancelled, int Page, int PageSize, ExpenseNature? Nature = null);

/// <summary>Uma página, ordenada por data prevista crescente e Id crescente.</summary>
public sealed record PlannedTransactionPage(IReadOnlyList<FinancialPlannedTransactionDto> Items, int Page, int PageSize, int TotalCount);

public enum PlannedTransactionAccess
{
    Allowed,

    /// <summary>Família ou previsão inexistente, ou usuário sem participação (404).</summary>
    NotFound,

    /// <summary>Dados inválidos (400).</summary>
    Invalid,

    /// <summary>Estado não permite a operação, com mensagem (409): cancelada ou realizada.</summary>
    Conflict,
}

public sealed record PlannedTransactionResult(
    PlannedTransactionAccess Access, FinancialPlannedTransactionDto? Planned, IDictionary<string, string[]>? Errors, string? Message = null)
{
    public const string CancelledMessage = "Previsão cancelada não pode ser editada.";

    public const string RealizedMessage =
        "Previsão realizada não pode ser editada nem cancelada. Para corrigir, cancele o lançamento efetivo: a previsão volta a ficar pendente.";


    public static PlannedTransactionResult NotFound { get; } = new(PlannedTransactionAccess.NotFound, null, null);

    public static PlannedTransactionResult Ok(FinancialPlannedTransactionDto planned) => new(PlannedTransactionAccess.Allowed, planned, null);

    public static PlannedTransactionResult Invalid(IDictionary<string, string[]> errors) => new(PlannedTransactionAccess.Invalid, null, errors);

    public static PlannedTransactionResult Conflict(string message) => new(PlannedTransactionAccess.Conflict, null, null, message);
}

public sealed record PlannedTransactionListResult(PlannedTransactionPage? Page, IDictionary<string, string[]>? Errors, bool NotFound)
{
    public static PlannedTransactionListResult NotFoundResult { get; } = new(null, null, true);
}

public enum RealizeAccess
{
    /// <summary>Lançamento efetivo criado agora (201).</summary>
    Created,

    /// <summary>Repetição com o mesmo valor e data: devolve o lançamento já existente, sem alterar nada (200).</summary>
    Existing,

    /// <summary>Família ou previsão inexistente, ou usuário sem participação (404).</summary>
    NotFound,

    /// <summary>Valor ou data inválidos, ou conta/categoria da previsão inativas (400).</summary>
    Invalid,

    /// <summary>Previsão cancelada, ou já realizada com outro valor ou data (409).</summary>
    Conflict,
}

public sealed record RealizeResult(
    RealizeAccess Access, FinancialTransactionDto? Transaction, IDictionary<string, string[]>? Errors, string? Message)
{
    public const string CancelledMessage = "Previsão cancelada não pode ser realizada.";

    public const string AlreadyRealizedMessage =
        "A previsão já foi realizada com outro valor ou data. Para corrigir, cancele o lançamento efetivo e realize de novo.";

}
