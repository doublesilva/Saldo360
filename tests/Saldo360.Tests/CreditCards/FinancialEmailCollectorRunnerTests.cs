using Saldo360.Application.CreditCards;
using Saldo360.Domain.CreditCards;

namespace Saldo360.Tests.CreditCards;

public sealed class FinancialEmailCollectorRunnerTests
{
    private static readonly Guid FamilyId = Guid.NewGuid(); private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public async Task HistoricalFailureAndContentionNeverCreateOrFailIncrementalCheckpoint()
    {
        var options = Options(); options.Enabled = false;
        var checkpoints = new FakeCheckpoints();
        var window = FinancialEmailHistoricalWindow.Create("2026-10-01", "2026-10-31");
        var collector = new FakeCollector { Throw = true };
        var runner = new FinancialEmailCollectorRunner(options, collector, new FakeLock(true), checkpoints, new FakeInbox(), TimeProvider.System);
        var result = await runner.RunHistoricalAsync("Itau", UserId, false, window, default);
        Assert.Equal("Failed", result!.Status); Assert.Equal("Historical", result.Mode);
        Assert.Null(checkpoints.Value); Assert.Equal(0, checkpoints.Saves);
        Assert.Null(await runner.RunHistoricalAsync("Itau", Guid.Empty, true, window, default));
        Assert.Null(await runner.RunHistoricalAsync("Itau", Guid.NewGuid(), true, window, default));
        Assert.Null(await runner.RunHistoricalAsync("Unknown", UserId, true, window, default));
        var blocked = new FinancialEmailCollectorRunner(options, collector, new FakeLock(false), checkpoints, new FakeInbox(), TimeProvider.System);
        Assert.Equal("SkippedAlreadyRunning", (await blocked.RunHistoricalAsync("Itau", UserId, false, window, default))!.Status);
        Assert.Null(checkpoints.Value); Assert.Equal(0, checkpoints.Saves);
    }

    [Fact]
    public async Task DistributedLockSkipsConcurrentProfileWithoutCallingCollectorOrCheckpoint()
    {
        var collector = new FakeCollector(); var checkpoints = new FakeCheckpoints();
        var runner = new FinancialEmailCollectorRunner(Options(), collector, new FakeLock(false), checkpoints, new FakeInbox(), TimeProvider.System);
        var result = await runner.RunAsync("Itau", UserId, false, CancellationToken.None);
        Assert.Equal("SkippedAlreadyRunning", result!.Status); Assert.Equal(0, collector.Calls); Assert.Equal(0, checkpoints.Saves);
    }

    [Fact]
    public async Task ProviderFailureBecomesOperationalFailureWithoutEscaping()
    {
        var collector = new FakeCollector { Throw = true }; var checkpoints = new FakeCheckpoints();
        var runner = new FinancialEmailCollectorRunner(Options(), collector, new FakeLock(true), checkpoints, new FakeInbox(), TimeProvider.System);
        var result = await runner.RunAsync("Itau", null, false, CancellationToken.None);
        Assert.Equal("Failed", result!.Status); Assert.Equal("Failed", checkpoints.Value!.LastResult); Assert.Equal(1, checkpoints.Saves);
    }

    [Fact]
    public async Task ManualDryRunRemainsAvailableWhileSchedulerIsDisabled()
    {
        var options = Options(); options.Enabled = false;
        var collector = new FakeCollector();
        var runner = new FinancialEmailCollectorRunner(options, collector, new FakeLock(true), new FakeCheckpoints(), new FakeInbox(), TimeProvider.System);

        var result = await runner.RunAsync("Itau", UserId, true, CancellationToken.None);

        Assert.NotNull(result); Assert.Equal(1, collector.Calls);
    }

    [Fact]
    public async Task ScheduledRunStaysDisabledWhileManualValidationIsAllowed()
    {
        var options = Options(); options.Enabled = false;
        var collector = new FakeCollector();
        var runner = new FinancialEmailCollectorRunner(options, collector, new FakeLock(true), new FakeCheckpoints(), new FakeInbox(), TimeProvider.System);

        var result = await runner.RunAsync("Itau", null, false, CancellationToken.None);

        Assert.Null(result); Assert.Equal(0, collector.Calls);
    }

    private static FinancialEmailImportOptions Options() => new() { Enabled = true, Sources = [new() { Name = "Itau", FamilyId = FamilyId, Enabled = true }] };
    private sealed class FakeCollector : IFinancialEmailCollector
    {
        public int Calls { get; private set; } public bool Throw { get; init; }
        public Task<FinancialEmailCollectorResult?> RunAsync(string profileName, Guid userId, bool dryRun, CancellationToken cancellationToken) => Run(profileName);
        public Task<FinancialEmailCollectorResult?> RunScheduledAsync(string profileName, CancellationToken cancellationToken) => Run(profileName);
        public Task<FinancialEmailCollectorResult?> RunHistoricalAsync(string profileName, Guid userId, bool dryRun, FinancialEmailHistoricalWindow window, CancellationToken cancellationToken) => Run(profileName);
        private Task<FinancialEmailCollectorResult?> Run(string profile) { Calls++; if (Throw) throw new HttpRequestException(); return Task.FromResult<FinancialEmailCollectorResult?>(new(profile, false, "Success", 1, 0, 0, 0, 0, 0, 0, 0, 0, [], [])); }
    }
    private sealed class FakeLock(bool acquired) : IFinancialEmailCollectorLock
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(Guid familyId, string profileName, CancellationToken cancellationToken) => Task.FromResult<IAsyncDisposable?>(acquired ? new Lease() : null);
        private sealed class Lease : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }
    private sealed class FakeCheckpoints : IFinancialEmailCollectorCheckpointRepository
    {
        public FinancialEmailCollectorCheckpoint? Value { get; private set; } public int Saves { get; private set; }
        public Task<FinancialEmailCollectorCheckpoint> GetOrCreateAsync(Guid familyId, string profileName, CancellationToken cancellationToken) => Task.FromResult(Value ??= FinancialEmailCollectorCheckpoint.Create(familyId, profileName));
        public Task<IReadOnlyList<FinancialEmailCollectorCheckpoint>> ListAsync(IReadOnlyCollection<Guid> familyIds, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FinancialEmailCollectorCheckpoint>>(Value is null ? [] : [Value]);
        public Task SaveAsync(CancellationToken cancellationToken) { Saves++; return Task.CompletedTask; }
    }
    private sealed class FakeInbox : IFinancialDocumentInboxRepository
    {
        public Task<bool> CanAccessFamilyAsync(Guid familyId, Guid userId, CancellationToken cancellationToken) => Task.FromResult(familyId == FamilyId && userId == UserId);
        public Task<bool> FamilyExistsAsync(Guid familyId, CancellationToken cancellationToken) => Task.FromResult(familyId == FamilyId);
        public Task<FinancialDocumentInboxItem?> FindByHashAsync(Guid familyId, string hash, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FinancialDocumentInboxItem?> FindByExternalIdAsync(Guid familyId, FinancialDocumentSource source, string externalId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<FinancialDocumentInboxItem?> GetAsync(Guid familyId, Guid id, Guid userId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<FinancialDocumentInboxItem>> ListAsync(Guid familyId, Guid userId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryAddAsync(FinancialDocumentInboxItem item, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
