using Saldo360.Domain.Goals;
namespace Saldo360.Application.Goals;
public interface IFinancialGoalRepository
{
    Task AddAsync(FinancialGoal goal, CancellationToken ct);
    Task<FinancialGoal?> FindAsync(Guid workspaceId, Guid id, CancellationToken ct);
    Task<FinancialGoalDto?> GetAsync(Guid workspaceId, Guid id, CancellationToken ct);
    Task<IReadOnlyList<FinancialGoalDto>> ListAsync(Guid workspaceId, bool includeArchived, CancellationToken ct);
    Task SaveAsync(CancellationToken ct);
}
