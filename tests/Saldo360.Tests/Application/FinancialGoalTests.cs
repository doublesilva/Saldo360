using Saldo360.Domain.Goals;

namespace Saldo360.Tests.Application;

[Trait("Category", "CORE")]
public sealed class FinancialGoalTests
{
    private static FinancialGoal Goal() => FinancialGoal.Create(Guid.NewGuid(), new FinancialGoalDetails("Reserva", "Emergência", FinancialGoalType.EmergencyFund, 12500.34m, new DateOnly(2027, 12, 31), FinancialGoalPriority.High), DateTimeOffset.UnixEpoch);

    [Fact]
    public void Creates_with_exact_decimal_and_active_status()
    {
        var goal = Goal();
        Assert.Equal(12500.34m, goal.TargetAmount);
        Assert.Equal(FinancialGoalStatus.Active, goal.Status);
        Assert.Equal(new DateOnly(2027, 12, 31), goal.TargetDate);
    }

    [Fact]
    public void Supports_pause_resume_and_completion_transitions()
    {
        var goal = Goal();
        goal.Pause(DateTimeOffset.UnixEpoch.AddDays(1));
        Assert.Equal(FinancialGoalStatus.Paused, goal.Status);
        goal.Resume(DateTimeOffset.UnixEpoch.AddDays(2));
        goal.Complete(DateTimeOffset.UnixEpoch.AddDays(3));
        Assert.Equal(FinancialGoalStatus.Completed, goal.Status);
        Assert.NotNull(goal.CompletedAt);
        Assert.Throws<InvalidOperationException>(() => goal.Resume(DateTimeOffset.UnixEpoch.AddDays(4)));
    }

    [Fact]
    public void Archive_preserves_history_and_blocks_editing()
    {
        var goal = Goal();
        goal.Archive(DateTimeOffset.UnixEpoch.AddDays(1));
        Assert.Equal(FinancialGoalStatus.Archived, goal.Status);
        Assert.NotNull(goal.ArchivedAt);
        Assert.Throws<InvalidOperationException>(() => goal.Update(new FinancialGoalDetails("Novo", null, FinancialGoalType.Other, 1m, null, FinancialGoalPriority.Low), DateTimeOffset.UnixEpoch.AddDays(2)));
    }
}
