using Saldo360.Domain.Goals;
namespace Saldo360.Application.Goals;
public interface IFinancialGoalAllocationRepository
{
 Task<FinancialGoalAllocationDto?> GetByOperationAsync(Guid workspaceId,Guid operationId,CancellationToken ct);
 Task<bool> PayloadMatchesAsync(Guid workspaceId,Guid operationId,Guid goalId,Guid accountId,FinancialGoalAllocationType type,decimal amount,DateOnly date,string? description,CancellationToken ct);
 Task<GoalAllocationResult> TryCreateAsync(FinancialGoalAllocation allocation,decimal targetAmount,CancellationToken ct);
 Task<IReadOnlyList<FinancialGoalAllocationDto>> ListAsync(Guid workspaceId,Guid goalId,CancellationToken ct);
 Task<GoalProgressDto?> ProgressAsync(Guid workspaceId,Guid goalId,decimal targetAmount,CancellationToken ct);
 Task<GoalAccountAvailabilityDto?> AvailabilityAsync(Guid workspaceId,Guid accountId,CancellationToken ct);
 Task<IReadOnlyList<GoalAccountAvailabilityDto>> ListAccountsAvailabilityAsync(Guid workspaceId,CancellationToken ct);
 Task<FinancialGoalSummaryDto> SummaryAsync(Guid workspaceId,CancellationToken ct);
 Task<FinancialGoalAllocation?> FindAsync(Guid workspaceId,Guid goalId,Guid allocationId,CancellationToken ct);
 Task SaveAsync(CancellationToken ct);
}
