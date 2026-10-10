using Saldo360.Domain.Accounts;

namespace Saldo360.Application.Accounts;

/// <summary>
/// Conta como exibida aos integrantes da família. Não expõe o criador nem dados de autenticação.
/// Não há saldo nesta etapa.
/// </summary>
public sealed record FinancialAccountDto(
    Guid Id,
    string Name,
    string? Institution,
    AccountType Type,
    FinancialScope FinancialScope,
    AccountResponsibleDto? Responsible,
    bool IsActive,
    string Currency,
    DateTimeOffset CreatedAt);

/// <summary>Responsável: só o Id do vínculo familiar e o nome de exibição. Null = conta conjunta.</summary>
public sealed record AccountResponsibleDto(Guid MemberId, string DisplayName);

/// <summary>Dados cadastrais enviados pelo cliente (texto bruto, validado pelo serviço).</summary>
public sealed record AccountInput(
    string? Name,
    string? Institution,
    string? Type,
    string? FinancialScope,
    Guid? ResponsibleMemberId);

public enum AccountAccess
{
    Allowed,

    /// <summary>Família ou conta inexistente, ou usuário sem participação (404).</summary>
    NotFound,

    /// <summary>Dados inválidos (400).</summary>
    Invalid,
}

public sealed record AccountResult(AccountAccess Access, FinancialAccountDto? Account, IDictionary<string, string[]>? Errors)
{
    public static AccountResult NotFound { get; } = new(AccountAccess.NotFound, null, null);

    public static AccountResult Ok(FinancialAccountDto account) => new(AccountAccess.Allowed, account, null);

    public static AccountResult Invalid(IDictionary<string, string[]> errors) => new(AccountAccess.Invalid, null, errors);
}
