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

public sealed record FinancialBudgetExecutionSourceTarget(
    Guid TargetId,
    Guid CategoryId,
    decimal Planned,
    decimal Realized,
    decimal Committed);

public sealed record FinancialBudgetExecutionDto(
    Guid BudgetId,
    DateOnly Competence,
    FinancialBudgetExecutionSummary Total,
    IReadOnlyList<FinancialBudgetTargetExecutionDto> Targets);

public sealed record FinancialBudgetTargetExecutionDto(
    Guid TargetId,
    Guid CategoryId,
    FinancialBudgetExecutionAmounts Amounts,
    FinancialBudgetAlertStatus Status,
    string AlertMessage);

public sealed record FinancialBudgetExecutionAmounts(
    decimal Planned,
    decimal Realized,
    decimal Committed,
    decimal ProjectedConsumption,
    decimal Available,
    decimal ExceededAmount,
    decimal? ConsumptionPercentage);

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
    int ExceededCount);

