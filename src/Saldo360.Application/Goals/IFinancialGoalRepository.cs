using Saldo360.Domain.Goals;
namespace Saldo360.Application.Goals;
public interface IFinancialGoalRepository
{
    Task AddAsync(FinancialGoal goal, CancellationToken ct);
    Task<FinancialGoal?> FindAsync(Guid familyId, Guid id, CancellationToken ct);
    Task<FinancialGoalDto?> GetAsync(Guid familyId, Guid id, CancellationToken ct);
    Task<IReadOnlyList<FinancialGoalDto>> ListAsync(Guid familyId, bool includeArchived, CancellationToken ct);
    Task SaveAsync(CancellationToken ct);
}
