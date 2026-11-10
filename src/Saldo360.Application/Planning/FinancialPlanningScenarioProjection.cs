using Saldo360.Application.Accounts;
using Saldo360.Application.Calendar;
using Saldo360.Application.Dashboard;
using Saldo360.Application.Dashboard.Overview;
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
    bool GoalAllocationCoverageComplete,
    // T14.1: a recuperação é da CONTA-ALVO. Ajustes só a alteram quando atribuídos a ela; transferências previstas já estão no saldo da conta (Agenda).
    decimal TargetAdjustment = 0m,                 // ajuste líquido do mês atribuído à conta-alvo
    decimal TargetCumulativeAdjustment = 0m,       // acumulado atribuído à conta-alvo
    decimal UnattributedAdjustment = 0m,           // ajuste líquido do mês que NÃO chega à conta-alvo (consolidado apenas ou outra conta)
    decimal? TargetOpeningBalance = null,          // saldo da conta-alvo no início do mês (cenário)
    decimal? TargetClosingBalance = null,          // saldo projetado da conta-alvo no fim do mês (cenário)
    decimal? TargetBaseClosingBalance = null,      // idem sem ajustes
    decimal TargetRealizedIncome = 0m, decimal TargetRealizedExpense = 0m, decimal TargetPendingIncome = 0m, decimal TargetPendingExpense = 0m,
    decimal? BaseClosingNegativeBalance = null,    // limite utilizado no fim do mês sem ajustes
    decimal? BaseProjectedRecovery = null,         // recuperação do mês sem ajustes
    decimal? ContractedLimit = null,
    decimal? RecoveryDeviation = null,             // recuperação do cenário − meta
    decimal? OfficialRecoveryTarget = null,        // meta oficial já fixada (somente leitura; simulação nunca a altera)
    string RecoveryTargetSource = "None");         // Official | BaseProjection | None

public sealed record PlanningScenarioProjection(Guid ScenarioId, DateOnly From, int Months, IReadOnlyList<PlanningScenarioProjectionMonth> Items);
public sealed record PlanningScenarioProjectionResult(PlanningScenarioProjection? Projection, IDictionary<string, string[]>? Errors, bool NotFound)
{
    public static PlanningScenarioProjectionResult NotFoundResult { get; } = new(null, null, true);
}

public sealed class FinancialPlanningScenarioProjectionService(
    IFinancialWorkspaceRepository workspaces,
    IFinancialPlanningScenarioRepository scenarios,
    FinancialCalendarMonthlyProjectionService monthlyProjection,
    IFinancialGoalAllocationRepository allocations,
    IOverdraftRecoveryRepository officialTargets,
    FinancialAccountService accounts)
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
        var targetCumulative = 0m;
        decimal? initialNegative = null;
        decimal? contractedLimit = scenario.RecoveryAccountId is { } limitAccount ? (await accounts.GetAsync(workspaceId, limitAccount, userId, ct))?.OverdraftLimit : null;
        var rows = new List<PlanningScenarioProjectionMonth>(scenario.Months);
        foreach (var item in baseResult.Projection!.Months)
        {
            parameters.TryGetValue(item.Month, out var parameter);
            var additionalIncome = parameter?.ExpectedAdditionalIncome ?? 0m;
            var additionalExpense = parameter?.ExpectedAdditionalExpense ?? 0m;
            var net = additionalIncome - additionalExpense;
            cumulative += net;
            var attributed = scenario.RecoveryAccountId is not null && parameter?.AdjustmentAccountId == scenario.RecoveryAccountId;
            var previousTargetCumulative = targetCumulative;
            if (attributed) targetCumulative += net;
            decimal? scenarioClosing = item.ClosingBalance is { } baseClosing ? baseClosing + cumulative : null;
            decimal? opening = item.OpeningBalance is { } baseOpening ? baseOpening + cumulative - (additionalIncome - additionalExpense) : null;
            var reserve = parameter?.ContingencyReserve ?? 0m;
            var target = accountProjection?.Months.FirstOrDefault(x => x.Month == item.Month);
            var targetComplete = scenario.RecoveryAccountId is { } && target is not null && target.UnavailableReason is null && target.OpeningBalance is not null && target.ClosingBalance is not null;
            decimal? openingNegative = null, closingNegative = null, baseOpeningNegative = null, baseClosingNegative = null, targetOpening = null, targetClosing = null, targetBaseClosing = null;
            if (targetComplete)
            {
                var previousTarget = accountProjection!.Months.FirstOrDefault(x => x.Month == item.Month.AddMonths(-1));
                var targetBaseOpening = previousTarget?.ClosingBalance ?? target!.OpeningBalance ?? 0m;
                targetBaseClosing = target!.ClosingBalance ?? 0m;
                // Só o que foi atribuído à conta-alvo muda o saldo dela; o saldo-base (inclusive transferências previstas) é o da Agenda.
                targetOpening = targetBaseOpening + previousTargetCumulative; targetClosing = targetBaseClosing + targetCumulative;
                baseOpeningNegative = OverdraftRecoveryRule.Used(targetBaseOpening); baseClosingNegative = OverdraftRecoveryRule.Used(targetBaseClosing.Value);
                openingNegative = OverdraftRecoveryRule.Used(targetOpening.Value); closingNegative = OverdraftRecoveryRule.Used(targetClosing.Value);
                initialNegative ??= openingNegative;
            }
            var complete = item.UnavailableReason is null && item.CreditCardProjectionComplete;
            var recoveryComplete = scenario.RecoveryAccountId is not null && complete && targetComplete && target!.CreditCardProjectionComplete;
            // Sem projeção confiável da conta, o limite utilizado também fica indisponível: nunca se apresenta um 0,00 fictício.
            if (!recoveryComplete) { openingNegative = closingNegative = baseOpeningNegative = baseClosingNegative = null; }
            decimal? baseRecovery = recoveryComplete ? Math.Min(baseOpeningNegative!.Value, Math.Max(0m, baseOpeningNegative.Value - baseClosingNegative!.Value)) : null;
            decimal? projectedRecovery = recoveryComplete ? Math.Min(openingNegative!.Value, Math.Max(0m, openingNegative.Value - closingNegative!.Value)) : null;
            decimal? cumulativeRecovery = recoveryComplete ? Math.Min(initialNegative!.Value, Math.Max(0m, initialNegative.Value - closingNegative!.Value)) : null;
            // Meta: a oficial já fixada (nunca reescrita) ou, sem ela, a regra de 80% sobre a capacidade da projeção-base. A simulação mede o desvio contra essa meta.
            decimal? capacity = recoveryComplete ? OverdraftRecoveryRule.Capacity(baseOpeningNegative!.Value, targetBaseClosing!.Value) : null;
            decimal? officialTarget = null; var targetSource = "None"; decimal? recoveryTarget = null;
            if (recoveryComplete)
            {
                officialTarget = (await officialTargets.GetAsync(workspaceId, scenario.RecoveryAccountId!.Value, item.Month, ct))?.RecoveryTarget;
                var percentage = parameter?.RecoveryTargetPercentage ?? 0m;
                recoveryTarget = officialTarget ?? decimal.Round(capacity!.Value * (percentage > 0m ? percentage / 100m : OverdraftRecoveryRule.TargetFactor), 2, MidpointRounding.AwayFromZero);
                targetSource = officialTarget is not null ? "Official" : "BaseProjection";
            }
            decimal? margin = recoveryComplete ? capacity - recoveryTarget : null;
            decimal? deviation = recoveryComplete ? projectedRecovery - recoveryTarget : null;
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
                goalSummary.CoverageComplete,
                attributed ? net : 0m, targetCumulative, attributed ? 0m : net, targetOpening, targetClosing, targetBaseClosing,
                target?.RealizedIncome ?? 0m, target?.RealizedExpense ?? 0m, target?.PendingIncome ?? 0m, target?.PendingExpense ?? 0m,
                baseClosingNegative, baseRecovery, contractedLimit, deviation, officialTarget, targetSource));
        }
        return new(new(scenario.Id, scenario.StartMonth, scenario.Months, rows), null, false);
    }
}
