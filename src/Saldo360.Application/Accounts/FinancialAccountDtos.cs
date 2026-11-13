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
    decimal? OverdraftLimit,
    bool IsActive,
    string Currency,
    DateTimeOffset CreatedAt,
    string Origin = AccountOrigins.Manual,
    AccountIntegrationDto? Integration = null,
    bool IsPrimary = false);

/// <summary>Origem do cadastro: digitado pelo usuário ou integrado a uma conta externa (T09.3). A origem é derivada do vínculo.</summary>
public static class AccountOrigins
{
    public const string Manual = "Manual";
    public const string Pluggy = "Pluggy";
}

/// <summary>Integração bancária da conta: instituição/nome exibido, situação, última sincronização e o saldo informado pelo banco (nunca o calculado).</summary>
public sealed record AccountIntegrationDto(
    Guid LinkId,
    Guid ConnectionId,
    string DisplayName,
    string? InstitutionName,
    string Status,
    DateTimeOffset? LastSyncedAt,
    DateOnly? ImportFrom,
    decimal? ObservedBalance,
    DateTimeOffset? ObservedAt);

/// <summary>Responsável: somente UserId e nome de exibição, sem vínculo familiar obrigatório.</summary>
public sealed record AccountResponsibleDto(Guid UserId, string DisplayName);

/// <summary>
/// Dados cadastrais enviados pelo cliente (texto bruto, validado pelo serviço). <c>IsPrimary</c>: true define a conta como principal
/// (substituindo a anterior), false remove a condição de principal e ausente não altera. <c>ReplacementPrimaryAccountId</c>
/// só vale ao desativar a conta principal: a substituta passa a ser a principal na mesma operação.
/// </summary>
public sealed record AccountInput(
    string? Name,
    string? Institution,
    string? Type,
    string? FinancialScope,
    Guid? ResponsibleUserId,
    decimal? OpeningBalance,
    DateOnly? OpeningBalanceDate,
    decimal? OverdraftLimit,
    bool? IsPrimary = null,
    Guid? ReplacementPrimaryAccountId = null);

/// <summary>Saldo inicial da conta; ambos nulos = não configurado.</summary>
/// <summary>Saldo inicial usado no cálculo. <c>Source</c>: Manual (cadastro) ou BalanceAnchor (derivado da referência bancária, sem cadastro manual).</summary>
public sealed record AccountOpeningBalance(decimal? Amount, DateOnly? Date, string? Source = null);

/// <summary>
/// Fluxo de caixa de uma conta num período: saldo de ABERTURA (início do primeiro dia; nulo com o motivo quando não há saldo inicial confiável, nunca zero) e os totais
/// efetivos do período. Lido no mesmo snapshot do saldo realizado.
/// </summary>
public sealed record AccountPeriodCashDto(Guid AccountId, string Name, bool IsActive, string Scope, decimal? Opening, string? OpeningReason, DateOnly? OpeningReferenceDate, AccountTotals Totals)
{
    public decimal? Closing => Opening is { } o ? FinancialBalanceCalculator.ApplyMovements(o, Totals.Income, Totals.Expense, Totals.TransferIn, Totals.TransferOut) : null;
}

/// <summary>Totais de uma conta no período: lançamentos (receita/despesa) e transferências (recebidas/enviadas).</summary>
/// <c>Refunds</c> é a parte de <c>Income</c> que são devoluções bancárias confirmadas (informativo: o saldo continua somando todo o <c>Income</c>).
public sealed record AccountTotals(decimal Income, decimal Expense, decimal TransferIn, decimal TransferOut, decimal Refunds = 0m);

/// <summary>
/// Entradas e saídas de uma conta no período: <c>Income</c>/<c>Expense</c> são receitas e despesas (sem transferências), <c>TransfersReceived</c>/<c>TransfersSent</c> as transferências da
/// conta, e <c>TotalInflows</c>/<c>TotalOutflows</c> a soma dos dois grupos (mostrados sempre separados). Mesmos movimentos e mesma regra do saldo realizado.
/// </summary>
public sealed record AccountFlowDto(Guid AccountId, string Name, bool IsActive, decimal Income, decimal Expense, decimal TransfersReceived, decimal TransfersSent, decimal Refunds = 0m)
{
    /// <summary><c>Income</c> inclui as devoluções bancárias (entrada de caixa que não é receita): <c>Revenue</c> é a receita de verdade, a mesma da Agenda e da Visão Geral.</summary>
    public decimal Revenue => Income - Refunds;
    public decimal TotalInflows => Income + TransfersReceived;
    public decimal TotalOutflows => Expense + TransfersSent;
}

/// <summary>
/// Fluxos do período por conta e do conjunto de todas as contas. No conjunto de todas as contas do orçamento toda transferência é interna
/// (<see cref="Saldo360.Application.Transfers.TransferPerspective"/>): <c>InternalTransfers</c> é só informativo e não entra em entradas nem saídas.
/// </summary>
public sealed record AccountFlowsDto(DateOnly From, DateOnly Through, IReadOnlyList<AccountFlowDto> Accounts, decimal ConsolidatedIncome, decimal ConsolidatedExpense, decimal InternalTransfers, decimal ConsolidatedRefunds = 0m);

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
    BalanceUnavailableReason? UnavailableReason,
    string? OpeningSource = null,
    decimal? OverdraftLimit = null,
    decimal? OverdraftUsed = null,
    decimal? OverdraftAvailable = null,
    decimal? OverdraftExcess = null);

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
