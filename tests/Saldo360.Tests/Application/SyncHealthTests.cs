using Saldo360.Application.Dashboard.PlanActual;
using Saldo360.Application.Pluggy;

namespace Saldo360.Tests.Application;

/// <summary>Contrato único de saúde da automação bancária: estados, motivos, precedência entre conexões e uso pelo Plano × Realizado. Dados fictícios, sem Pluggy.</summary>
[Trait("Category", "CORE")]
[Trait("Area", "Pluggy")]
public sealed class SyncHealthTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly SyncCadence Daily = new(true, false, 24);

    private static SyncHealthDto Evaluate(string status, DateTimeOffset? success, DateTimeOffset? attempt = null, SyncCadence? cadence = null) =>
        SyncHealth.Evaluate(status, success, attempt ?? success, cadence ?? Daily, Now);

    [Fact]
    public void ARecentImportWithACadenceIsUpToDate()
    {
        var health = Evaluate("Completed", Now.AddHours(-3));

        Assert.Equal((SyncHealthStates.UpToDate, null), (health.State, health.Reason));
        Assert.Equal(Now.AddHours(-3), health.LastSuccessAt);
    }

    [Fact]
    public void OnlyAConfiguredCadenceWithAnExpiredDeadlineIsDelayed()
    {
        var onTime = Evaluate("Completed", Now.AddHours(-36));
        var late = Evaluate("Completed", Now.AddHours(-37));

        Assert.Equal(SyncHealthStates.UpToDate, onTime.State);
        Assert.Equal((SyncHealthStates.Delayed, "ImportOverdue"), (late.State, late.Reason));
        Assert.Equal(Now.AddHours(-37).AddHours(36), late.OverdueSince);
    }

    [Theory]
    [InlineData(false, "AutomationOff")]
    [InlineData(true, "AutomationPaused")]
    public void WithoutACadenceAnOldImportIsAutomationDisabledNeverDelayed(bool paused, string reason)
    {
        var health = Evaluate("Completed", Now.AddDays(-20), cadence: new SyncCadence(false, paused, 24));

        Assert.Equal((SyncHealthStates.AutomationDisabled, reason), (health.State, health.Reason));
        Assert.Equal(Now.AddDays(-20), health.LastSuccessAt);
    }

    [Theory]
    [InlineData("NeverSynced")]
    [InlineData("Running")]
    public void WithoutAnyImportTheConnectionAwaitsTheFirstOneEvenWithTheAutomationOff(string status)
    {
        Assert.Equal(SyncHealthStates.AwaitingFirstImport, Evaluate(status, null).State);
        Assert.Equal(SyncHealthStates.AwaitingFirstImport, Evaluate(status, null, cadence: new SyncCadence(false, false, 24)).State);
        Assert.Null(Evaluate(status, null).LastSuccessAt);
    }

    [Fact]
    public void ConsentExpiredBlocksAndAFailedRunNeedsAttentionKeepingTheLastSuccess()
    {
        var blocked = Evaluate("ConsentExpired", Now.AddHours(-2));
        var failed = Evaluate("Failed", Now.AddHours(-2));
        var withErrors = Evaluate("CompletedWithErrors", Now.AddHours(-2));
        var failedNeverImported = Evaluate("Failed", null);

        Assert.Equal((SyncHealthStates.Blocked, "ConsentExpired"), (blocked.State, blocked.Reason));
        Assert.Equal((SyncHealthStates.NeedsAttention, "RunFailed"), (failed.State, failed.Reason));
        Assert.Equal((SyncHealthStates.NeedsAttention, "RunCompletedWithErrors"), (withErrors.State, withErrors.Reason));
        Assert.Equal(Now.AddHours(-2), failed.LastSuccessAt);
        Assert.Equal(SyncHealthStates.NeedsAttention, failedNeverImported.State);
    }

    [Fact]
    public void ASourceWithoutNewDataCheckedOnTimeIsNotDelayedButAStaleCheckIs()
    {
        var checkedRecently = Evaluate("SourceNotUpdated", Now.AddDays(-5), attempt: Now.AddHours(-2));
        var notChecked = Evaluate("SourceNotUpdated", Now.AddDays(-5), attempt: Now.AddDays(-4));

        Assert.Equal(SyncHealthStates.UpToDate, checkedRecently.State);
        Assert.Equal(Now.AddDays(-5), checkedRecently.LastSuccessAt);
        Assert.Equal(SyncHealthStates.Delayed, notChecked.State);
    }

    [Fact]
    public void WithSeveralConnectionsTheWorstStateWinsAndPointsToTheAffectedOnes()
    {
        var healthy = Guid.NewGuid();
        var delayed = Guid.NewGuid();
        var blocked = Guid.NewGuid();
        var summary = SyncHealth.Summarize([(healthy, Evaluate("Completed", Now.AddHours(-1))), (delayed, Evaluate("Completed", Now.AddDays(-5))), (blocked, Evaluate("ConsentExpired", Now.AddDays(-9)))])!;

        Assert.Equal((SyncHealthStates.Blocked, 3, 1, 1), (summary.State, summary.Connections, summary.InState, summary.UpToDate));
        Assert.Equal([blocked], summary.AffectedConnectionIds);
    }

    [Fact]
    public void OneHealthyAndOneProblematicConnectionHighlightOnlyTheAffectedOne()
    {
        var ok = new SyncConnectionDto(Guid.NewGuid(), "Banco A", "Completed", Now.AddHours(-2), Now.AddHours(-2), Now.AddHours(22), false);
        var late = new SyncConnectionDto(Guid.NewGuid(), "Banco B", "Completed", Now.AddDays(-4), Now.AddDays(-4), Now.AddHours(1), false);

        var summary = PlanActualCalculator.Sync([ok, late], true, 24, Now);

        Assert.Equal((SyncHealthStates.Delayed, 2, 1, 1), (summary.Health!.State, summary.Health.Connections, summary.Health.InState, summary.Health.UpToDate));
        Assert.Equal([late.ConnectionId], summary.Health.AffectedConnectionIds);
        Assert.Equal([SyncHealthStates.UpToDate, SyncHealthStates.Delayed], summary.Connections.Select(c => c.Health!.State).ToArray());
    }

    [Fact]
    public void AMixOfHealthyAndAwaitingFirstImportIsNeverReportedAsUpToDate()
    {
        var ok = new SyncConnectionDto(Guid.NewGuid(), "Banco A", "Completed", Now.AddHours(-2), Now.AddHours(-2), null, false);
        var fresh = new SyncConnectionDto(Guid.NewGuid(), "Banco B", "NeverSynced", null, null, null, false);

        var health = PlanActualCalculator.Sync([ok, fresh], true, 24, Now).Health!;

        Assert.Equal((SyncHealthStates.AwaitingFirstImport, 2, 1, 1), (health.State, health.Connections, health.InState, health.UpToDate));
        Assert.Equal([fresh.ConnectionId], health.AffectedConnectionIds);
    }

    [Fact]
    public void ThePausedAutomationOfTheWorkspaceSuppressesTheDelay()
    {
        var old = new SyncConnectionDto(Guid.NewGuid(), "Banco A", "Completed", Now.AddDays(-4), Now.AddDays(-4), null, false);

        Assert.Equal(SyncHealthStates.Delayed, PlanActualCalculator.Sync([old], true, 24, Now).Health!.State);
        Assert.Equal(SyncHealthStates.AutomationDisabled, PlanActualCalculator.Sync([old], true, 24, Now, paused: true).Health!.State);
    }

    [Fact]
    public void WithoutConnectionsThereIsNoSummary() => Assert.Null(SyncHealth.Summarize([]));
}
