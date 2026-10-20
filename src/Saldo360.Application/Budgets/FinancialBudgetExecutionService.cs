using Saldo360.Application.Workspaces;

namespace Saldo360.Application.Budgets;

public sealed class FinancialBudgetExecutionService(
    IFinancialWorkspaceRepository workspaces,
    IFinancialBudgetExecutionRepository executions)
{
    public const int PercentageScale = 2;
    public const decimal ApproachingThreshold = 0.80m;

    public async Task<FinancialBudgetExecutionDto?> GetAsync(
        Guid workspaceId, Guid budgetId, Guid userId, CancellationToken cancellationToken)
    {
        if (await workspaces.GetOwnerRoleAsync(workspaceId, userId, cancellationToken) is null) return null;
        var source = await executions.GetAsync(workspaceId, budgetId, cancellationToken);
        if (source is null) return null;

        var targets = source.Targets
            .Select(item =>
            {
                var amounts = Calculate(item.Planned, item.Realized, item.Committed, item.CreditCardCommitted);
                var status = Classify(item.Planned, amounts.ProjectedConsumption);
                return new FinancialBudgetTargetExecutionDto(
                    item.TargetId, item.CategoryId, amounts, status, Message(status));
            })
            .ToList();
        var amountsTotal = Calculate(
            source.Targets.Sum(item => item.Planned),
            source.Targets.Sum(item => item.Realized),
            source.Targets.Sum(item => item.Committed),
            source.Targets.Sum(item => item.CreditCardCommitted));
        var totalStatus = targets.Count == 0
            ? FinancialBudgetAlertStatus.WithinBudget
            : targets.Max(item => item.Status);
        var totalMessage = targets.Count == 0
            ? "Nenhuma meta ativa para esta competência."
            : Message(totalStatus);
        return new FinancialBudgetExecutionDto(source.BudgetId, source.Competence,
            new FinancialBudgetExecutionSummary(
                amountsTotal.Planned, amountsTotal.Realized, amountsTotal.Committed,
                amountsTotal.ProjectedConsumption, amountsTotal.Available, amountsTotal.ExceededAmount,
                amountsTotal.ConsumptionPercentage, totalStatus, totalMessage,
                targets.Count(item => item.Status == FinancialBudgetAlertStatus.ApproachingLimit),
                targets.Count(item => item.Status == FinancialBudgetAlertStatus.LimitReached),
                targets.Count(item => item.Status == FinancialBudgetAlertStatus.Exceeded),
                amountsTotal.CreditCardCommitted), targets);
    }

    public static FinancialBudgetAlertStatus Classify(decimal planned, decimal projected)
    {
        if (planned == 0m) return projected == 0m
            ? FinancialBudgetAlertStatus.WithinBudget
            : FinancialBudgetAlertStatus.Exceeded;
        var ratio = projected / planned;
        if (ratio > 1m) return FinancialBudgetAlertStatus.Exceeded;
        if (ratio == 1m) return FinancialBudgetAlertStatus.LimitReached;
        return ratio >= ApproachingThreshold
            ? FinancialBudgetAlertStatus.ApproachingLimit
            : FinancialBudgetAlertStatus.WithinBudget;
    }

    public static string Message(FinancialBudgetAlertStatus status) => status switch
    {
        FinancialBudgetAlertStatus.WithinBudget => "O consumo projetado está dentro do orçamento.",
        FinancialBudgetAlertStatus.ApproachingLimit => "O consumo projetado está próximo do limite.",
        FinancialBudgetAlertStatus.LimitReached => "O consumo projetado atingiu o limite do orçamento.",
        FinancialBudgetAlertStatus.Exceeded => "O consumo projetado ultrapassou o orçamento.",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    public static FinancialBudgetExecutionAmounts Calculate(decimal planned, decimal realized, decimal committed, decimal creditCardCommitted = 0m)
    {
        var projected = realized + committed + creditCardCommitted;
        var available = planned - projected;
        var exceeded = Math.Max(0m, projected - planned);
        decimal? percentage = planned == 0m
            ? projected == 0m ? 0m : null
            : decimal.Round(projected / planned * 100m, PercentageScale, MidpointRounding.AwayFromZero);
        return new FinancialBudgetExecutionAmounts(
            planned, realized, committed, projected, available, exceeded, percentage, creditCardCommitted);
    }
}

