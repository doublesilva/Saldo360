using Saldo360.Domain.Planning;

namespace Saldo360.Application.Planning;

public interface IFinancialPlanningScenarioRepository
{
    Task AddScenarioAsync(FinancialPlanningScenario scenario, CancellationToken ct);
    Task<FinancialPlanningScenario?> FindScenarioAsync(Guid workspaceId, Guid id, CancellationToken ct);
    Task<PlanningScenarioDto?> GetScenarioAsync(Guid workspaceId, Guid id, CancellationToken ct);
    Task<IReadOnlyList<PlanningScenarioDto>> ListScenariosAsync(Guid workspaceId, bool includeArchived, CancellationToken ct);
    Task<bool> ScenarioNameExistsAsync(Guid workspaceId, string name, Guid? exceptId, CancellationToken ct);
    Task SaveAsync(CancellationToken ct);
    Task AddParameterAsync(FinancialPlanningMonthlyParameter parameter, CancellationToken ct);
    Task<FinancialPlanningMonthlyParameter?> FindParameterAsync(Guid workspaceId, Guid scenarioId, DateOnly competence, CancellationToken ct);
    Task<IReadOnlyList<PlanningMonthlyParameterDto>> ListParametersAsync(Guid workspaceId, Guid scenarioId, CancellationToken ct);
}
