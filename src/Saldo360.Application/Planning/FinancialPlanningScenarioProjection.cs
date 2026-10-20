using Saldo360.Application.Calendar;
using Saldo360.Application.Workspaces;
using Saldo360.Application.Goals;

namespace Saldo360.Application.Planning;

public enum RecoveryStatus { NotConfigured, NoNegativeBalance, Recovering, Stable, Worsening, Recovered, IncompleteProjection }

public sealed record PlanningScenarioProjectionMonth(
    DateOnly Competence,
    decimal? OpeningBalance,
    decimal RealizedIncome,
    decimal RealizedExpense,
    decimal PendingIncome,
    decimal PendingExpense,
    decimal CreditCardOutstanding,
    decimal? BaseClosingBalance,
    decimal AdditionalIncome,
    decimal AdditionalExpense,
    decimal CumulativeAdjustment,
    decimal? ScenarioClosingBalance,
    decimal ContingencyReserve,
    decimal? PlannedAvailable,
    bool ProjectionComplete,
    bool UsesBaseProjectionOnly,
    bool HasUnallocatedObligations,
    decimal RecoveryTargetPercentage,
    Guid? RecoveryAccountId,
    decimal? OpeningNegativeBalance,
    decimal? ClosingNegativeBalance,
    decimal? ProjectedRecovery,
    decimal? CumulativeRecovery,
    decimal? RecoveryCapacity,
    decimal? RecoveryTarget,
    decimal? UncommittedMargin,
    decimal? RemainingNegativeBalance,
    RecoveryStatus RecoveryStatus,
    bool RecoveryProjectionComplete,
    decimal AllocatedToGoals,
    decimal? GoalProtectedAvailability,
    bool GoalAllocationCoverageComplete);

public sealed record PlanningScenarioProjection(Guid ScenarioId, DateOnly From, int Months, IReadOnlyList<PlanningScenarioProjectionMonth> Items);
public sealed record PlanningScenarioProjectionResult(PlanningScenarioProjection? Projection, IDictionary<string, string[]>? Errors, bool NotFound)
{
    public static PlanningScenarioProjectionResult NotFoundResult { get; } = new(null, null, true);
}

public sealed class FinancialPlanningScenarioProjectionService(
    IFinancialWorkspaceRepository workspaces,
    IFinancialPlanningScenarioRepository scenarios,
    FinancialCalendarMonthlyProjectionService monthlyProjection,
    IFinancialGoalAllocationRepository allocations)
{
    public async Task<PlanningScenarioProjectionResult> GetAsync(Guid workspaceId, Guid scenarioId, Guid userId, CancellationToken ct)
    {
        if (await workspaces.GetOwnerRoleAsync(workspaceId, userId, ct) is null) return PlanningScenarioProjectionResult.NotFoundResult;
        var scenario = await scenarios.GetScenarioAsync(workspaceId, scenarioId, ct);
        if (scenario is null) return PlanningScenarioProjectionResult.NotFoundResult;
        var baseResult = await monthlyProjection.GetAsync(workspaceId, userId, new FinancialCalendarMonthlyProjectionQuery(scenario.StartMonth, scenario.Months, null), ct);
        if (baseResult.NotFound) return PlanningScenarioProjectionResult.NotFoundResult;
        if (baseResult.Errors is not null) return new(null, baseResult.Errors, false);
        var parameters = (await scenarios.ListParametersAsync(workspaceId, scenarioId, ct)).ToDictionary(x => x.Competence);
        var goalSummary = await allocations.SummaryAsync(workspaceId, ct);
        FinancialCalendarMonthlyProjection? accountProjection = null;
        if (scenario.RecoveryAccountId is { } recoveryAccountId)
        {
            var accountResult = await monthlyProjection.GetAsync(workspaceId, userId, new FinancialCalendarMonthlyProjectionQuery(scenario.StartMonth, scenario.Months, recoveryAccountId), ct);
            if (!accountResult.NotFound && accountResult.Errors is null) accountProjection = accountResult.Projection;
        }
        var cumulative = 0m;
        decimal? initialNegative = null;
        var rows = new List<PlanningScenarioProjectionMonth>(scenario.Months);
        foreach (var item in baseResult.Projection!.Months)
        {
            parameters.TryGetValue(item.Month, out var parameter);
            var additionalIncome = parameter?.ExpectedAdditionalIncome ?? 0m;
            var additionalExpense = parameter?.ExpectedAdditionalExpense ?? 0m;
            cumulative += additionalIncome - additionalExpense;
            decimal? scenarioClosing = item.ClosingBalance is { } baseClosing ? baseClosing + cumulative : null;
            decimal? opening = item.OpeningBalance is { } baseOpening ? baseOpening + cumulative - (additionalIncome - additionalExpense) : null;
            var reserve = parameter?.ContingencyReserve ?? 0m;
            var target = accountProjection?.Months.FirstOrDefault(x => x.Month == item.Month);
            var targetComplete = scenario.RecoveryAccountId is { } && target is not null && target.UnavailableReason is null && target.OpeningBalance is not null && target.ClosingBalance is not null;
            decimal? openingNegative = null;
            decimal? closingNegative = null;
            if (targetComplete)
            {
                var previousTarget = accountProjection!.Months.FirstOrDefault(x => x.Month == item.Month.AddMonths(-1));
                var openingBalance = previousTarget?.ClosingBalance ?? target!.OpeningBalance;
                openingNegative = Math.Max(0m, -(openingBalance ?? 0m));
                closingNegative = Math.Max(0m, -(target!.ClosingBalance ?? 0m));
                initialNegative ??= openingNegative;
            }
            var complete = item.UnavailableReason is null && item.CreditCardProjectionComplete;
            var recoveryComplete = scenario.RecoveryAccountId is not null && complete && targetComplete;
            decimal? capacity = recoveryComplete && scenarioClosing is { } availableBase ? Math.Max(0m, availableBase - reserve) : null;
            decimal? projectedRecovery = recoveryComplete ? Math.Min(openingNegative!.Value, Math.Max(0m, openingNegative.Value - closingNegative!.Value)) : null;
            decimal? cumulativeRecovery = recoveryComplete ? Math.Min(initialNegative!.Value, Math.Max(0m, initialNegative.Value - closingNegative!.Value)) : null;
            decimal? recoveryTarget = recoveryComplete ? Math.Min(openingNegative!.Value, capacity!.Value * (parameter?.RecoveryTargetPercentage ?? 0m) / 100m) : null;
            decimal? margin = recoveryComplete ? capacity - recoveryTarget : null;
            var remaining = recoveryComplete ? closingNegative : null;
            var status = scenario.RecoveryAccountId is null ? RecoveryStatus.NotConfigured : !recoveryComplete ? RecoveryStatus.IncompleteProjection :
                openingNegative == 0m && closingNegative == 0m ? RecoveryStatus.NoNegativeBalance :
                closingNegative == 0m && openingNegative > 0m ? RecoveryStatus.Recovered :
                closingNegative < openingNegative ? RecoveryStatus.Recovering : closingNegative > openingNegative ? RecoveryStatus.Worsening : RecoveryStatus.Stable;
            rows.Add(new(item.Month, opening, item.RealizedIncome, item.RealizedExpense, item.PendingIncome, item.PendingExpense,
                item.CreditCardOutstanding ?? 0m, item.ClosingBalance, additionalIncome, additionalExpense, cumulative, scenarioClosing,
                reserve, scenarioClosing is { } closing ? closing - reserve : null,
                complete, parameter is null, !item.CreditCardProjectionComplete, parameter?.RecoveryTargetPercentage ?? 0m,
                scenario.RecoveryAccountId, openingNegative, closingNegative, projectedRecovery, cumulativeRecovery, capacity, recoveryTarget, margin,
                remaining, status, recoveryComplete, goalSummary.AllocatedToGoals,
                scenarioClosing is { } protectedClosing ? protectedClosing - goalSummary.AllocatedToGoals - reserve : null,
                goalSummary.CoverageComplete));
        }
        return new(new(scenario.Id, scenario.StartMonth, scenario.Months, rows), null, false);
    }
}
