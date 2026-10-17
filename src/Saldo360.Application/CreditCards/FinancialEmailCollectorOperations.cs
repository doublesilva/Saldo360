namespace Saldo360.Application.CreditCards;

public interface IFinancialEmailCollectorLock
{
    Task<IAsyncDisposable?> TryAcquireAsync(Guid familyId, string profileName, CancellationToken cancellationToken);
}

public sealed class FinancialEmailCollectorRunner(
    FinancialEmailImportOptions options,
    IFinancialEmailCollector collector,
    IFinancialEmailCollectorLock distributedLock,
    IFinancialEmailCollectorCheckpointRepository checkpoints,
    IFinancialDocumentInboxRepository inbox,
    TimeProvider clock)
{
    public async Task<FinancialEmailCollectorResult?> RunAsync(string profileName, Guid? userId, bool dryRun, CancellationToken cancellationToken)
    {
        var profile = options.Sources.SingleOrDefault(item => item.Enabled && string.Equals(item.Name, profileName, StringComparison.OrdinalIgnoreCase));
        if (!options.Enabled || profile is null) return null;
        if (userId is { } actor && !await inbox.CanAccessFamilyAsync(profile.FamilyId, actor, cancellationToken)) return null;
        await using var lease = await distributedLock.TryAcquireAsync(profile.FamilyId, profile.Name, cancellationToken);
        if (lease is null)
        {
            return new(profile.Name, dryRun, "SkippedAlreadyRunning", 0, 0, 0, 0, 0, 0, 0, 0, 0, [], []);
        }
        var started = clock.GetUtcNow();
        try { return userId is { } id ? await collector.RunAsync(profile.Name, id, dryRun, cancellationToken) : await collector.RunScheduledAsync(profile.Name, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            if (!dryRun)
            {
                var checkpoint = await checkpoints.GetOrCreateAsync(profile.FamilyId, profile.Name, cancellationToken);
                checkpoint.Fail(clock.GetUtcNow(), (long)(clock.GetUtcNow() - started).TotalMilliseconds);
                await checkpoints.SaveAsync(cancellationToken);
            }
            return new(profile.Name, dryRun, "Failed", (long)(clock.GetUtcNow() - started).TotalMilliseconds, 0, 0, 0, 0, 0, 0, 0, 1, [], [new("collector", null, "ProviderUnavailable")]);
        }
    }
}

public sealed class FinancialEmailSchedule
{
    private readonly TimeZoneInfo zone;
    private readonly IReadOnlyList<TimeOnly> times;
    public FinancialEmailSchedule(FinancialEmailImportOptions options)
    {
        zone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZone);
        times = options.Schedule.Select(value => TimeOnly.ParseExact(value, "HH:mm")).Distinct().Order().ToList();
        if (times.Count == 0) throw new InvalidOperationException("FinancialEmailImport:Schedule deve possuir ao menos um horário.");
    }
    public DateTimeOffset Next(DateTimeOffset now)
    {
        var localNow = TimeZoneInfo.ConvertTime(now, zone); var date = DateOnly.FromDateTime(localNow.DateTime);
        for (var day = 0; day < 2; day++)
            foreach (var time in times)
            {
                var local = date.AddDays(day).ToDateTime(time, DateTimeKind.Unspecified);
                var candidate = new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
                if (candidate > now) return candidate;
            }
        throw new InvalidOperationException("Não foi possível calcular a próxima execução.");
    }
    public DateTimeOffset Previous(DateTimeOffset now)
    {
        var localNow = TimeZoneInfo.ConvertTime(now, zone); var date = DateOnly.FromDateTime(localNow.DateTime);
        var candidates = new List<DateTimeOffset>();
        for (var day = -1; day <= 0; day++) foreach (var time in times)
        {
            var local = date.AddDays(day).ToDateTime(time, DateTimeKind.Unspecified);
            var candidate = new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime(); if (candidate <= now) candidates.Add(candidate);
        }
        return candidates.Max();
    }
}

public sealed record FinancialEmailCollectorStatusDto(string Profile, Guid FamilyId, bool Enabled, string? LastRunAt,
    string? LastSuccessfulRunAt, long? DurationMilliseconds, string Status, int MessagesScanned, int CandidateAttachments,
    int AcceptedDocuments, int Duplicates, int NeedsReview, int Failures, string NextRunAt, bool Delayed);

public sealed class FinancialEmailCollectorStatusService(FinancialEmailImportOptions options,
    IFinancialEmailCollectorCheckpointRepository checkpoints, IFinancialDocumentInboxRepository inbox, FinancialEmailSchedule schedule, TimeProvider clock)
{
    public async Task<IReadOnlyList<FinancialEmailCollectorStatusDto>> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var profiles = new List<FinancialEmailImportProfile>();
        foreach (var profile in options.Sources)
            if (await inbox.CanAccessFamilyAsync(profile.FamilyId, userId, cancellationToken)) profiles.Add(profile);
        var states = await checkpoints.ListAsync(profiles.Select(item => item.FamilyId).Distinct().ToList(), cancellationToken);
        var now = clock.GetUtcNow(); var next = schedule.Next(now); var previous = schedule.Previous(now);
        return profiles.Select(profile =>
        {
            var state = states.SingleOrDefault(item => item.FamilyId == profile.FamilyId && item.ProfileName == profile.Name);
            var delayed = options.Enabled && profile.Enabled && now > previous.AddHours(1) && (state?.LastCompletedAt is null || state.LastCompletedAt < previous);
            return new FinancialEmailCollectorStatusDto(profile.Name, profile.FamilyId, options.Enabled && profile.Enabled,
                state?.LastStartedAt?.ToString("O"), state?.LastSuccessfulRunAt?.ToString("O"), state?.LastCompletedAt is null ? null : state.LastDurationMilliseconds,
                state?.LastResult ?? "NeverRun", state?.LastMessagesScanned ?? 0, state?.LastCandidateAttachments ?? 0,
                state?.LastAcceptedDocuments ?? 0, state?.LastDuplicates ?? 0, state?.LastNeedsReview ?? 0, state?.LastFailures ?? 0,
                next.ToString("O"), delayed);
        }).ToList();
    }
}
