namespace Saldo360.Application.Budgets;

public sealed record FinancialBudgetExecutionSource(
    Guid BudgetId,
    DateOnly Competence,
    IReadOnlyList<FinancialBudgetExecutionSourceTarget> Targets);

public sealed record FinancialBudgetExecutionSourceTarget(
    Guid TargetId,
    Guid CategoryId,
    decimal Planned,
    decimal Realized,
    decimal Committed);

public sealed record FinancialBudgetExecutionDto(
    Guid BudgetId,
    DateOnly Competence,
    FinancialBudgetExecutionAmounts Total,
    IReadOnlyList<FinancialBudgetTargetExecutionDto> Targets);

public sealed record FinancialBudgetTargetExecutionDto(
    Guid TargetId,
    Guid CategoryId,
    FinancialBudgetExecutionAmounts Amounts);

public sealed record FinancialBudgetExecutionAmounts(
    decimal Planned,
    decimal Realized,
    decimal Committed,
    decimal ProjectedConsumption,
    decimal Available,
    decimal ExceededAmount,
    decimal? ConsumptionPercentage);

