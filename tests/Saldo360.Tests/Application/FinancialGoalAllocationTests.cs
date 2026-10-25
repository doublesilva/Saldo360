using Saldo360.Domain.Goals;

namespace Saldo360.Tests.Application;

[Trait("Category", "CORE")]
public sealed class FinancialGoalAllocationTests
{
    [Fact]
    public void Contribution_and_withdrawal_keep_operation_identity_and_cancel_logically()
    {
        var family = Guid.NewGuid(); var goal = Guid.NewGuid(); var account = Guid.NewGuid(); var operation = Guid.NewGuid();
        var allocation = FinancialGoalAllocation.Create(family, goal, account, FinancialGoalAllocationType.Contribution, 500.25m, new DateOnly(2026, 10, 16), operation, "reserva", DateTimeOffset.UnixEpoch);
        Assert.Equal(operation, allocation.OperationId);
        Assert.Equal(500.25m, allocation.Amount);
        allocation.Cancel(DateTimeOffset.UnixEpoch.AddDays(1));
        var cancelled = allocation.CancelledAt;
        allocation.Cancel(DateTimeOffset.UnixEpoch.AddDays(2));
        Assert.Equal(cancelled, allocation.CancelledAt);
    }
}
