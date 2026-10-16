using Saldo360.Domain.Planning;

namespace Saldo360.Application.Planning;

public interface IFinancialPlanningScenarioRepository
{
    Task AddScenarioAsync(FinancialPlanningScenario scenario, CancellationToken ct);
    Task<FinancialPlanningScenario?> FindScenarioAsync(Guid familyId, Guid id, CancellationToken ct);
    Task<PlanningScenarioDto?> GetScenarioAsync(Guid familyId, Guid id, CancellationToken ct);
    Task<IReadOnlyList<PlanningScenarioDto>> ListScenariosAsync(Guid familyId, bool includeArchived, CancellationToken ct);
    Task<bool> ScenarioNameExistsAsync(Guid familyId, string name, Guid? exceptId, CancellationToken ct);
    Task SaveAsync(CancellationToken ct);
    Task AddParameterAsync(FinancialPlanningMonthlyParameter parameter, CancellationToken ct);
    Task<FinancialPlanningMonthlyParameter?> FindParameterAsync(Guid familyId, Guid scenarioId, DateOnly competence, CancellationToken ct);
    Task<IReadOnlyList<PlanningMonthlyParameterDto>> ListParametersAsync(Guid familyId, Guid scenarioId, CancellationToken ct);
}
