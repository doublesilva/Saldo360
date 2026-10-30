using Saldo360.Domain.Budgets;

namespace Saldo360.Application.Budgets;

public enum FinancialBudgetAlertStatus
{
    WithinBudget,
    ApproachingLimit,
    LimitReached,
    Exceeded,
}

public sealed record FinancialBudgetExecutionSource(
    Guid BudgetId,
    DateOnly Competence,
    IReadOnlyList<FinancialBudgetExecutionSourceTarget> Targets);

/// <summary>
/// Meta e o que a consome na competência. <c>Realized</c> = despesa efetiva fora do cartão; <c>CreditCardCommitted</c> = compras/parcelas já lançadas nos
/// cartões (consumidas mesmo com a fatura em aberto); <c>Committed</c> = compromissos pendentes ainda não realizados (previsto adicional).
/// </summary>
public sealed record FinancialBudgetExecutionSourceTarget(
    Guid TargetId,
    Guid? CategoryId,
    decimal Planned,
    decimal Realized,
    decimal Committed,
    decimal CreditCardCommitted = 0m,
    BudgetTargetGroupingType GroupingType = BudgetTargetGroupingType.Category,
    BudgetCoverage? Coverage = null);

/// <summary>
/// Execução do orçamento do mês. As perspectivas (categoria, despesas fixas, todos os cartões) são independentes e podem contar o mesmo gasto: por isso
/// só as metas de categoria (que não se sobrepõem) têm um resumo conjunto; fixas e cartões nunca são somados a elas nem entre si.
/// </summary>
public sealed record FinancialBudgetExecutionDto(
    Guid BudgetId,
    DateOnly Competence,
    FinancialBudgetExecutionSummary Categories,
    IReadOnlyList<FinancialBudgetTargetExecutionDto> Targets);

public sealed record FinancialBudgetTargetExecutionDto(
    Guid TargetId,
    Guid? CategoryId,
    FinancialBudgetExecutionAmounts Amounts,
    FinancialBudgetAlertStatus Status,
    string AlertMessage,
    BudgetTargetGroupingType GroupingType = BudgetTargetGroupingType.Category,
    FinancialBudgetAlertStatus ProjectedStatus = FinancialBudgetAlertStatus.WithinBudget,
    BudgetCoverage? Coverage = null);

/// <summary>
/// <c>Consumed</c> = efetivo + cartão lançado; <c>Available</c> = limite − consumido; <c>ProjectedConsumption</c> = consumido + previsto adicional
/// (<c>Committed</c>); <c>AvailableProjected</c> = limite − projetado. Limite zero não divide: percentual nulo se houver consumo.
/// </summary>
public sealed record FinancialBudgetExecutionAmounts(
    decimal Planned,
    decimal Realized,
    decimal Committed,
    decimal ProjectedConsumption,
    decimal Available,
    decimal ExceededAmount,
    decimal? ConsumptionPercentage,
    decimal CreditCardCommitted = 0m,
    decimal Consumed = 0m,
    decimal AvailableProjected = 0m,
    decimal ExceededProjected = 0m,
    decimal? ProjectedPercentage = null,
    FinancialBudgetAlertStatus ProjectedStatus = FinancialBudgetAlertStatus.WithinBudget);

public sealed record FinancialBudgetExecutionSummary(
    decimal Planned,
    decimal Realized,
    decimal Committed,
    decimal ProjectedConsumption,
    decimal Available,
    decimal ExceededAmount,
    decimal? ConsumptionPercentage,
    FinancialBudgetAlertStatus Status,
    string AlertMessage,
    int ApproachingCount,
    int ReachedCount,
    int ExceededCount,
    decimal CreditCardCommitted = 0m,
    decimal Consumed = 0m,
    decimal AvailableProjected = 0m,
    int TargetCount = 0,
    decimal ExceededProjected = 0m,
    decimal? ProjectedPercentage = null,
    FinancialBudgetAlertStatus ProjectedStatus = FinancialBudgetAlertStatus.WithinBudget);

/// <summary>Um registro da composição de uma meta, com a mesma lista que gerou o resumo.</summary>
public sealed record BudgetCompositionItemDto(
    BudgetFactOrigin Origin,
    Guid RecordId,
    DateOnly Date,
    decimal Amount,
    BudgetFactState State,
    Guid? CategoryId,
    string? CategoryPath,
    Saldo360.Domain.Categories.ExpenseNature Nature,
    string Description,
    Guid? CardId,
    string? CardName,
    string? AccountName,
    string? InvoiceCompetence,
    bool CompetenceReliable);

public sealed record BudgetCompositionCardDto(Guid CardId, string CardName, decimal Consumed, int Count);

public sealed record BudgetCompositionDto(
    Guid BudgetId,
    DateOnly Competence,
    Guid TargetId,
    BudgetTargetGroupingType GroupingType,
    FinancialBudgetExecutionAmounts Amounts,
    FinancialBudgetAlertStatus Status,
    BudgetCoverage Coverage,
    int Page,
    int PageSize,
    int TotalCount,
    IReadOnlyList<BudgetCompositionItemDto> Items,
    IReadOnlyList<BudgetCompositionCardDto> Cards);
