using Saldo360.Application.Families;

namespace Saldo360.Application.Budgets;

public sealed class FinancialBudgetExecutionService(
    IFamilyRepository families,
    IFinancialBudgetExecutionRepository executions)
{
    public const int PercentageScale = 2;

    public async Task<FinancialBudgetExecutionDto?> GetAsync(
        Guid familyId, Guid budgetId, Guid userId, CancellationToken cancellationToken)
    {
        if (await families.GetMemberRoleAsync(familyId, userId, cancellationToken) is null) return null;
        var source = await executions.GetAsync(familyId, budgetId, cancellationToken);
        if (source is null) return null;

        var targets = source.Targets
            .Select(item => new FinancialBudgetTargetExecutionDto(
                item.TargetId, item.CategoryId, Calculate(item.Planned, item.Realized, item.Committed)))
            .ToList();
        var total = Calculate(
            source.Targets.Sum(item => item.Planned),
            source.Targets.Sum(item => item.Realized),
            source.Targets.Sum(item => item.Committed));
        return new FinancialBudgetExecutionDto(source.BudgetId, source.Competence, total, targets);
    }

    internal static FinancialBudgetExecutionAmounts Calculate(decimal planned, decimal realized, decimal committed)
    {
        var projected = realized + committed;
        var available = planned - projected;
        var exceeded = Math.Max(0m, projected - planned);
        decimal? percentage = planned == 0m
            ? projected == 0m ? 0m : null
            : decimal.Round(projected / planned * 100m, PercentageScale, MidpointRounding.AwayFromZero);
        return new FinancialBudgetExecutionAmounts(
            planned, realized, committed, projected, available, exceeded, percentage);
    }
}

