using Saldo360.Domain.Accounts;

namespace Saldo360.Application.Accounts;

/// <summary>
/// Conta do workspace pessoal. Não expõe o criador nem dados de autenticação.
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

/// <summary>Responsável: somente UserId e nome de exibição, sem vínculo familiar obrigatório.</summary>
public sealed record AccountResponsibleDto(Guid UserId, string DisplayName);

/// <summary>Dados cadastrais enviados pelo cliente (texto bruto, validado pelo serviço).</summary>
public sealed record AccountInput(
    string? Name,
    string? Institution,
    string? Type,
    string? FinancialScope,
    Guid? ResponsibleUserId,
    decimal? OpeningBalance,
    DateOnly? OpeningBalanceDate);

/// <summary>Saldo inicial da conta; ambos nulos = não configurado.</summary>
public sealed record AccountOpeningBalance(decimal? Amount, DateOnly? Date);

/// <summary>Totais de uma conta no período: lançamentos (receita/despesa) e transferências (recebidas/enviadas).</summary>
public sealed record AccountTotals(decimal Income, decimal Expense, decimal TransferIn, decimal TransferOut);

/// <summary>
/// Previsões PENDENTES da conta até a data-alvo, por tipo: vencidas (DueDate &lt; data-base) e do período
/// (data-base ≤ DueDate ≤ data-alvo). Canceladas e realizadas (com lançamento ativo) ficam de fora.
/// </summary>
public sealed record PlannedTotals(decimal OverdueIncome, decimal OverdueExpense, decimal PlannedIncome, decimal PlannedExpense);

/// <summary>
/// Saldo projetado: parte do saldo realizado no fechamento de <see cref="BaseDate"/> (hoje em America/Sao_Paulo) e
/// soma só previsões pendentes até <see cref="Through"/>:
/// ProjectedBalance = RealizedBalance + OverdueIncome − OverdueExpense + PlannedIncome − PlannedExpense.
/// Se o saldo realizado estiver indisponível, todos os valores são nulos e <see cref="UnavailableReason"/> diz por quê.
/// </summary>
public sealed record AccountProjectionDto(
    Guid AccountId,
    DateOnly BaseDate,
    DateOnly Through,
    decimal? RealizedBalance,
    decimal? OverdueIncome,
    decimal? OverdueExpense,
    decimal? PlannedIncome,
    decimal? PlannedExpense,
    decimal? ProjectedBalance,
    BalanceUnavailableReason? UnavailableReason,
    decimal? CreditCardOutstanding = null,
    bool CreditCardProjectionComplete = true);

public sealed record CreditCardProjectionTotals(decimal Outstanding, bool HasUnallocated);

public sealed record ProjectionResult(BalanceAccess Access, AccountProjectionDto? Projection, IDictionary<string, string[]>? Errors);

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
/// − despesas + transferências recebidas − transferências enviadas, todas não canceladas e com
/// OpeningBalanceDate ≤ data ≤ AsOf (período desta conta). Indisponível: totais e saldo nulos.
/// </summary>
public sealed record AccountBalanceDto(
    Guid AccountId,
    DateOnly AsOf,
    decimal? OpeningBalance,
    DateOnly? OpeningBalanceDate,
    decimal? TotalIncome,
    decimal? TotalExpense,
    decimal? TotalTransferIn,
    decimal? TotalTransferOut,
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
