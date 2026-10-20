using Saldo360.Domain.Goals;
namespace Saldo360.Application.Goals;
public sealed record FinancialGoalAllocationDto(Guid Id,Guid WorkspaceId,Guid GoalId,Guid FinancialAccountId,FinancialGoalAllocationType OperationType,decimal Amount,DateOnly EffectiveDate,Guid OperationId,string? Description,DateTimeOffset CreatedAt,DateTimeOffset? CancelledAt);
public sealed record GoalAllocationInput(Guid? OperationId,Guid? FinancialAccountId,string? Amount,DateOnly? EffectiveDate,string? Description);
public sealed record GoalProgressDto(Guid GoalId,decimal TargetAmount,decimal AllocatedAmount,decimal RemainingToTarget,decimal ProgressPercentage,bool IsFunded);
public sealed record GoalAccountAvailabilityDto(Guid FinancialAccountId,decimal RealizedBalance,decimal AllocatedAmount,decimal UnallocatedBalance);
public enum GoalAllocationAccess { Allowed, NotFound, Invalid, Conflict }
public sealed record GoalAllocationResult(GoalAllocationAccess Access,FinancialGoalAllocationDto? Allocation,IDictionary<string,string[]>? Errors,string? Message=null);
