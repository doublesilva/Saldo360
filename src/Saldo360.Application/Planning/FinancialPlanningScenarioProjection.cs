using Saldo360.Application.Calendar;
using Saldo360.Application.Families;

namespace Saldo360.Application.Planning;

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
    decimal RecoveryTargetPercentage);

public sealed record PlanningScenarioProjection(Guid ScenarioId, DateOnly From, int Months, IReadOnlyList<PlanningScenarioProjectionMonth> Items);
public sealed record PlanningScenarioProjectionResult(PlanningScenarioProjection? Projection, IDictionary<string, string[]>? Errors, bool NotFound)
{
    public static PlanningScenarioProjectionResult NotFoundResult { get; } = new(null, null, true);
}

public sealed class FinancialPlanningScenarioProjectionService(
    IFamilyRepository families,
    IFinancialPlanningScenarioRepository scenarios,
    FinancialCalendarMonthlyProjectionService monthlyProjection)
{
    public async Task<PlanningScenarioProjectionResult> GetAsync(Guid familyId, Guid scenarioId, Guid userId, CancellationToken ct)
    {
        if (await families.GetMemberRoleAsync(familyId, userId, ct) is null) return PlanningScenarioProjectionResult.NotFoundResult;
        var scenario = await scenarios.GetScenarioAsync(familyId, scenarioId, ct);
        if (scenario is null) return PlanningScenarioProjectionResult.NotFoundResult;
        var baseResult = await monthlyProjection.GetAsync(familyId, userId, new FinancialCalendarMonthlyProjectionQuery(scenario.StartMonth, scenario.Months, null), ct);
        if (baseResult.NotFound) return PlanningScenarioProjectionResult.NotFoundResult;
        if (baseResult.Errors is not null) return new(null, baseResult.Errors, false);
        var parameters = (await scenarios.ListParametersAsync(familyId, scenarioId, ct)).ToDictionary(x => x.Competence);
        var cumulative = 0m;
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
            rows.Add(new(item.Month, opening, item.RealizedIncome, item.RealizedExpense, item.PendingIncome, item.PendingExpense,
                item.CreditCardOutstanding ?? 0m, item.ClosingBalance, additionalIncome, additionalExpense, cumulative, scenarioClosing,
                reserve, scenarioClosing is { } closing ? closing - reserve : null,
                item.UnavailableReason is null && item.CreditCardProjectionComplete,
                parameter is null, !item.CreditCardProjectionComplete, parameter?.RecoveryTargetPercentage ?? 0m));
        }
        return new(new(scenario.Id, scenario.StartMonth, scenario.Months, rows), null, false);
    }
}
