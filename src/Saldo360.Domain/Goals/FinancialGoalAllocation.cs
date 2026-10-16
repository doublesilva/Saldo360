using Saldo360.Domain.Common;
namespace Saldo360.Domain.Goals;
public enum FinancialGoalAllocationType { Contribution, Withdrawal }
public sealed class FinancialGoalAllocation : Entity
{
    public const int DescriptionMaxLength = 500;
    private FinancialGoalAllocation() { }
    public Guid FamilyId { get; private init; }
    public Guid GoalId { get; private init; }
    public Guid FinancialAccountId { get; private init; }
    public FinancialGoalAllocationType OperationType { get; private init; }
    public decimal Amount { get; private init; }
    public DateOnly EffectiveDate { get; private init; }
    public Guid OperationId { get; private init; }
    public string? Description { get; private init; }
    public DateTimeOffset CreatedAt { get; private init; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public static FinancialGoalAllocation Create(Guid familyId,Guid goalId,Guid accountId,FinancialGoalAllocationType type,decimal amount,DateOnly date,Guid operationId,string? description,DateTimeOffset now)=>new(){FamilyId=familyId,GoalId=goalId,FinancialAccountId=accountId,OperationType=type,Amount=amount,EffectiveDate=date,OperationId=operationId,Description=description,CreatedAt=now};
    public void Cancel(DateTimeOffset now){if(CancelledAt is null)CancelledAt=now;}
}
