using Saldo360.Domain.Accounts;

namespace Saldo360.Application.Accounts;

/// <summary>
/// Conta como exibida aos integrantes da família. Não expõe o criador nem dados de autenticação.
/// Traz só o saldo inicial configurado (valor no início da data de referência); não há saldo atual nesta etapa.
/// </summary>
public sealed record FinancialAccountDto(
    Guid Id,
    string Name,
    string? Institution,
    AccountType Type,
    FinancialScope FinancialScope,
    AccountResponsibleDto? Responsible,
    decimal? OpeningBalance,
    DateOnly? OpeningBalanceDate,
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
    Guid? ResponsibleMemberId,
    decimal? OpeningBalance,
    DateOnly? OpeningBalanceDate);

/// <summary>Saldo inicial da conta; ambos nulos = não configurado.</summary>
public sealed record AccountOpeningBalance(decimal? Amount, DateOnly? Date);

public sealed record AccountTotals(decimal Income, decimal Expense);

/// <summary>Por que o saldo realizado não pode ser calculado. Nunca é tratado como zero.</summary>
public enum BalanceUnavailableReason
{
    /// <summary>A conta não tem saldo inicial (valor e data de referência).</summary>
    OpeningBalanceNotConfigured,

    /// <summary>A data consultada é anterior à data de referência do saldo inicial.</summary>
    BeforeOpeningBalanceDate,
}

/// <summary>
/// Saldo realizado no FECHAMENTO de <see cref="AsOf"/>: saldo inicial (início de OpeningBalanceDate) + receitas
/// − despesas não canceladas com OpeningBalanceDate ≤ data ≤ AsOf. Indisponível: totais e saldo nulos.
/// </summary>
public sealed record AccountBalanceDto(
    Guid AccountId,
    DateOnly AsOf,
    decimal? OpeningBalance,
    DateOnly? OpeningBalanceDate,
    decimal? TotalIncome,
    decimal? TotalExpense,
    decimal? Balance,
    BalanceUnavailableReason? UnavailableReason);

public enum BalanceAccess
{
    Allowed,

    /// <summary>Família ou conta inexistente, ou usuário sem participação (404).</summary>
    NotFound,

    /// <summary>Data futura (400).</summary>
    Invalid,
}

public sealed record BalanceResult(BalanceAccess Access, AccountBalanceDto? Balance, IDictionary<string, string[]>? Errors);

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
