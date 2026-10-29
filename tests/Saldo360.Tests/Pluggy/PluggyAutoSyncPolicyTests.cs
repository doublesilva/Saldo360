using Saldo360.Application.Pluggy;
using Saldo360.Domain.Pluggy;

namespace Saldo360.Tests.Pluggy;

/// <summary>T09.6: regras puras de quando sincronizar e quando tentar de novo, sem banco nem rede.</summary>
[Trait("Category", "CORE")]
[Trait("Area", "Pluggy")]
public sealed class PluggyAutoSyncPolicyTests
{
    private static readonly PluggyAutoSyncOptions Defaults = new();
    // 12:00 UTC = 09:00 em Brasília.
    private static readonly DateTimeOffset Morning = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, 24, true)]   // sem restrição
    [InlineData(2, 6, false)]   // madrugada: 09h está fora
    [InlineData(8, 10, true)]
    [InlineData(10, 8, false)]  // atravessa a meia-noite: 09h cai no intervalo proibido
    [InlineData(22, 10, true)]
    public void TheWindowIsEvaluatedInBrasiliaTime(int start, int end, bool expected) =>
        Assert.Equal(expected, PluggyAutoSyncPolicy.IsInWindow(Morning, new() { WindowStartHour = start, WindowEndHour = end }));

    [Fact]
    public void WhenClosedTheNextWindowOpensAtTheConfiguredStartHour()
    {
        var options = new PluggyAutoSyncOptions { WindowStartHour = 2, WindowEndHour = 6 };

        var next = PluggyAutoSyncPolicy.NextWindowOpen(Morning, options);

        Assert.Equal(new DateTimeOffset(2026, 10, 30, 2, 0, 0, TimeSpan.FromHours(-3)), next);
        Assert.Equal(Morning, PluggyAutoSyncPolicy.NextWindowOpen(Morning, Defaults)); // aberta: vale agora
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    public void RetriesWaitLongerEachTimeUntilTheLimit(int failures, int minutes) =>
        Assert.Equal(Morning.AddMinutes(minutes), PluggyAutoSyncPolicy.AfterFailure(Morning, failures, Defaults));

    [Fact]
    public void WhenTheAttemptsAreExhaustedTheNextTryIsTheRegularInterval()
    {
        Assert.Equal(Morning.AddHours(24), PluggyAutoSyncPolicy.AfterFailure(Morning, 3, Defaults));
        Assert.Equal(Morning.AddHours(12), PluggyAutoSyncPolicy.AfterSuccess(Morning, new() { IntervalHours = 12 }));
    }

    [Fact]
    public void TheBackoffNeverExceedsTheConfiguredCeiling() =>
        Assert.Equal(Morning.AddMinutes(45), PluggyAutoSyncPolicy.AfterFailure(Morning, 4, new() { RetryDelayMinutes = 30, MaxRetryDelayMinutes = 45, MaxConsecutiveAttempts = 9 }));

    [Fact]
    public void RecheckingAnUnchangedSourceOrAnExpiredConsentIsCheapAndFrequentEnoughToNoticeRenewal()
    {
        Assert.Equal(Morning.AddMinutes(120), PluggyAutoSyncPolicy.AfterSourceNotUpdated(Morning, Defaults));
        Assert.Equal(Morning.AddHours(6), PluggyAutoSyncPolicy.AfterConsentExpired(Morning, Defaults));
        Assert.Equal(Morning.AddMinutes(5), PluggyAutoSyncPolicy.AfterBusy(Morning, Defaults));
    }

    [Fact]
    public void TheSourceIsOnlyConsideredUnchangedAfterACompleteSyncWithAKnownUpdateInstant()
    {
        var known = Morning.AddHours(-30);
        var completed = StateWith(PluggySyncOutcome.Completed, known);

        Assert.True(PluggyAutoSyncPolicy.SourceHasNothingNew(completed, known));
        Assert.True(PluggyAutoSyncPolicy.SourceHasNothingNew(completed, known.AddHours(-1)));
        Assert.False(PluggyAutoSyncPolicy.SourceHasNothingNew(completed, known.AddSeconds(1)));   // a instituição atualizou
        Assert.False(PluggyAutoSyncPolicy.SourceHasNothingNew(completed, null));                   // sem informação da origem: sincroniza
        Assert.False(PluggyAutoSyncPolicy.SourceHasNothingNew(null, known));                       // nunca sincronizou
        Assert.False(PluggyAutoSyncPolicy.SourceHasNothingNew(StateWith(PluggySyncOutcome.CompletedWithErrors, known), known)); // última coleta incompleta: repete
        Assert.False(PluggyAutoSyncPolicy.SourceHasNothingNew(StateWith(PluggySyncOutcome.Failed, known), known));
    }

    [Fact]
    public void ConsentIsFlaggedOnlyBetweenNowAndTheWarningWindow()
    {
        Assert.True(PluggyAutoSyncPolicy.ConsentExpiringSoon(Morning.AddDays(3), Morning, Defaults));
        Assert.False(PluggyAutoSyncPolicy.ConsentExpiringSoon(Morning.AddDays(30), Morning, Defaults));
        Assert.False(PluggyAutoSyncPolicy.ConsentExpiringSoon(Morning.AddDays(-1), Morning, Defaults)); // já vencido tem outro estado
        Assert.False(PluggyAutoSyncPolicy.ConsentExpiringSoon(null, Morning, Defaults));
    }

    private static PluggySyncState StateWith(PluggySyncOutcome outcome, DateTimeOffset sourceUpdatedAt)
    {
        var state = PluggySyncState.Create(Guid.NewGuid(), Guid.NewGuid(), Morning);
        state.RecordAttempt(Morning, outcome, PluggySyncTrigger.Scheduled, null, null, outcome is PluggySyncOutcome.Failed or PluggySyncOutcome.CompletedWithErrors, null, sourceUpdatedAt);
        // O instante da origem só é guardado numa sincronização completa; nos demais casos o teste o fixa pelo caminho completo antes.
        if (outcome != PluggySyncOutcome.Completed) { var completed = PluggySyncState.Create(state.WorkspaceId, state.ConnectionId, Morning); completed.RecordAttempt(Morning, PluggySyncOutcome.Completed, PluggySyncTrigger.Scheduled, null, null, false, null, sourceUpdatedAt); completed.RecordAttempt(Morning, outcome, PluggySyncTrigger.Scheduled, null, null, true, null, null); return completed; }
        return state;
    }
}
