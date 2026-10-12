using Saldo360.Domain.Accounts;

namespace Saldo360.Application.Transfers;

/// <summary>
/// Transferência como exibida aos integrantes da família. Origem e destino vêm com nome, escopo (PF/PJ) e
/// situação, para o histórico continuar legível depois de desativar contas. Não expõe família nem usuários.
/// </summary>
public sealed record FinancialTransferDto(
    Guid Id,
    decimal Amount,
    DateOnly TransferDate,
    string Description,
    TransferAccountDto SourceAccount,
    TransferAccountDto DestinationAccount,
    bool IsCancelled,
    string Currency,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CancelledAt);

public sealed record TransferAccountDto(Guid Id, string Name, FinancialScope FinancialScope, bool IsActive);

/// <summary>Dados enviados pelo cliente (brutos, validados pelo serviço).</summary>
public sealed record TransferInput(
    Guid? SourceAccountId,
    Guid? DestinationAccountId,
    decimal? Amount,
    DateOnly? TransferDate,
    string? Description);

/// <summary>Filtros brutos da listagem (datas já no formato ISO, validadas na API).</summary>
public sealed record TransferQuery(DateOnly? From, DateOnly? To, Guid? AccountId, bool IncludeCancelled, int? Page, int? PageSize);

/// <summary>Filtros validados. AccountId casa com origem OU destino.</summary>
public sealed record TransferFilter(DateOnly? From, DateOnly? To, Guid? AccountId, bool IncludeCancelled, int Page, int PageSize);

/// <summary>Uma página, ordenada por data decrescente e Id decrescente.</summary>
public sealed record TransferPage(IReadOnlyList<FinancialTransferDto> Items, int Page, int PageSize, int TotalCount);

public enum TransferAccess
{
    Allowed,

    /// <summary>Família ou transferência inexistente, ou usuário sem participação (404).</summary>
    NotFound,

    /// <summary>Dados inválidos (400).</summary>
    Invalid,
}

public sealed record TransferResult(TransferAccess Access, FinancialTransferDto? Transfer, IDictionary<string, string[]>? Errors)
{
    public static TransferResult NotFound { get; } = new(TransferAccess.NotFound, null, null);

    public static TransferResult Ok(FinancialTransferDto transfer) => new(TransferAccess.Allowed, transfer, null);

    public static TransferResult Invalid(IDictionary<string, string[]> errors) => new(TransferAccess.Invalid, null, errors);
}

public sealed record TransferListResult(TransferPage? Page, IDictionary<string, string[]>? Errors, bool NotFound)
{
    public static TransferListResult NotFoundResult { get; } = new(null, null, true);
}
