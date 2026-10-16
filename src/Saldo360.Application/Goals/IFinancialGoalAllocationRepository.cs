using Saldo360.Domain.Goals;
namespace Saldo360.Application.Goals;
public interface IFinancialGoalAllocationRepository
{
 Task<FinancialGoalAllocationDto?> GetByOperationAsync(Guid familyId,Guid operationId,CancellationToken ct);
 Task<bool> PayloadMatchesAsync(Guid familyId,Guid operationId,Guid goalId,Guid accountId,FinancialGoalAllocationType type,decimal amount,DateOnly date,string? description,CancellationToken ct);
 Task<GoalAllocationResult> TryCreateAsync(FinancialGoalAllocation allocation,decimal targetAmount,CancellationToken ct);
 Task<IReadOnlyList<FinancialGoalAllocationDto>> ListAsync(Guid familyId,Guid goalId,CancellationToken ct);
 Task<GoalProgressDto?> ProgressAsync(Guid familyId,Guid goalId,decimal targetAmount,CancellationToken ct);
 Task<GoalAccountAvailabilityDto?> AvailabilityAsync(Guid familyId,Guid accountId,CancellationToken ct);
 Task<IReadOnlyList<GoalAccountAvailabilityDto>> ListAccountsAvailabilityAsync(Guid familyId,CancellationToken ct);
 Task<FinancialGoalAllocation?> FindAsync(Guid familyId,Guid goalId,Guid allocationId,CancellationToken ct);
 Task SaveAsync(CancellationToken ct);
}
