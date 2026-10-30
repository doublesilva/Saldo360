using Microsoft.EntityFrameworkCore;
using Npgsql;
using Saldo360.Application.Pluggy;
using Saldo360.Domain.Common;
using Saldo360.Domain.Pluggy;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Pluggy;

/// <summary>
/// Preferências de automação e histórico de execuções (R04). O histórico une as execuções de sincronização que já existiam (<c>pluggy_sync_runs</c>) às de
/// categorização, registradas a partir desta entrega: nada é inventado para o passado.
/// </summary>
internal sealed class PluggyAutomationRepository(Saldo360DbContext db) : IPluggyAutomationRepository
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById(BusinessCalendar.TimeZoneId);

    public Task<PluggyAutomationSettings?> FindSettingsAsync(Guid workspaceId, CancellationToken ct) =>
        db.PluggyAutomationSettings.AsNoTracking().SingleOrDefaultAsync(s => s.WorkspaceId == workspaceId, ct);

    public async Task<PluggyAutomationSettings> SaveSettingsAsync(Guid workspaceId, Guid userId, Action<PluggyAutomationSettings> change, DateTimeOffset now, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var settings = await db.PluggyAutomationSettings.SingleOrDefaultAsync(s => s.WorkspaceId == workspaceId, ct);
            var created = settings is null;
            if (created) { settings = PluggyAutomationSettings.Create(workspaceId, userId, now); db.PluggyAutomationSettings.Add(settings); }
            change(settings!);
            try { await db.SaveChangesAsync(ct); db.Entry(settings!).State = EntityState.Detached; return settings!; }
            catch (DbUpdateException exception) when (created && attempt < 2 && exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                db.Entry(settings!).State = EntityState.Detached; // outra requisição criou a linha primeiro: reaplica sobre ela
            }
        }
    }

    public async Task<PluggyCategorizationRun?> TryStartCategorizationAsync(Guid workspaceId, Guid userId, PluggyCategorizationTrigger trigger, Guid? syncRunId, Guid? connectionId, bool usedAi,
        DateTimeOffset now, TimeSpan staleAfter, CancellationToken ct)
    {
        var staleBefore = now - staleAfter;
        await db.PluggyCategorizationRuns.Where(r => r.WorkspaceId == workspaceId && r.State == PluggyCategorizationRunState.Running && r.StartedAt < staleBefore)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.State, PluggyCategorizationRunState.Failed).SetProperty(r => r.FinishedAt, now).SetProperty(r => r.FailureCode, "Abandoned"), ct);
        var run = PluggyCategorizationRun.Start(workspaceId, userId, trigger, syncRunId, connectionId, usedAi, now);
        db.PluggyCategorizationRuns.Add(run);
        try { await db.SaveChangesAsync(ct); db.Entry(run).State = EntityState.Detached; return run; }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.Entry(run).State = EntityState.Detached;
            return null;
        }
    }

    public async Task SaveCategorizationAsync(PluggyCategorizationRun run, CancellationToken ct)
    {
        if (db.Entry(run).State == EntityState.Detached) db.PluggyCategorizationRuns.Update(run);
        await db.SaveChangesAsync(ct);
        db.Entry(run).State = EntityState.Detached;
    }

    public Task<int> RecoverAbandonedCategorizationsAsync(DateTimeOffset now, TimeSpan staleAfter, CancellationToken ct)
    {
        var staleBefore = now - staleAfter;
        return db.PluggyCategorizationRuns.Where(r => r.State == PluggyCategorizationRunState.Running && r.StartedAt < staleBefore)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.State, PluggyCategorizationRunState.Failed).SetProperty(r => r.FinishedAt, now).SetProperty(r => r.FailureCode, "Abandoned"), ct);
    }

    public async Task<(IReadOnlyList<AutomationRunRow> Items, int Total)> ListRunsAsync(Guid workspaceId, AutomationRunFilter filter, DateTimeOffset staleBefore, CancellationToken ct)
    {
        var fromUtc = filter.From is { } from ? StartOfDay(from) : (DateTimeOffset?)null;
        var toUtc = filter.To is { } to ? StartOfDay(to.AddDays(1)) : (DateTimeOffset?)null;
        var take = filter.Page * filter.PageSize;
        var rows = new List<AutomationRunRow>();
        var total = 0;

        if (filter.Kind is null or AutomationKinds.Sync)
        {
            var query = db.PluggySyncRuns.AsNoTracking().Where(r => r.WorkspaceId == workspaceId);
            if (fromUtc is { } f) query = query.Where(r => r.StartedAt >= f);
            if (toUtc is { } t) query = query.Where(r => r.StartedAt < t);
            query = filter.State switch
            {
                "Running" => query.Where(r => r.Status == PluggySyncStatus.Running && r.StartedAt >= staleBefore),
                "Completed" => query.Where(r => r.Status == PluggySyncStatus.Completed),
                "CompletedWithPending" => query.Where(r => r.Status == PluggySyncStatus.CompletedWithErrors),
                "Failed" => query.Where(r => r.Status == PluggySyncStatus.Failed || (r.Status == PluggySyncStatus.Running && r.StartedAt < staleBefore)),
                _ => query,
            };
            total += await query.CountAsync(ct);
            rows.AddRange((await query.OrderByDescending(r => r.StartedAt).ThenBy(r => r.Id).Take(take).ToListAsync(ct)).Select(ToRow));
        }
        if (filter.Kind is null or AutomationKinds.Categorization)
        {
            var query = db.PluggyCategorizationRuns.AsNoTracking().Where(r => r.WorkspaceId == workspaceId);
            if (fromUtc is { } f) query = query.Where(r => r.StartedAt >= f);
            if (toUtc is { } t) query = query.Where(r => r.StartedAt < t);
            query = filter.State switch
            {
                "Running" => query.Where(r => r.State == PluggyCategorizationRunState.Running && r.StartedAt >= staleBefore),
                "Completed" => query.Where(r => r.State == PluggyCategorizationRunState.Completed),
                "CompletedWithPending" => query.Where(r => r.State == PluggyCategorizationRunState.CompletedWithPending),
                "Failed" => query.Where(r => r.State == PluggyCategorizationRunState.Failed || (r.State == PluggyCategorizationRunState.Running && r.StartedAt < staleBefore)),
                _ => query,
            };
            total += await query.CountAsync(ct);
            rows.AddRange((await query.OrderByDescending(r => r.StartedAt).ThenBy(r => r.Id).Take(take).ToListAsync(ct)).Select(ToRow));
        }
        var page = rows.OrderByDescending(r => r.StartedAt).ThenBy(r => r.Id).Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize).ToList();
        return (page, total);
    }

    public async Task<AutomationRunRow?> FindRunAsync(Guid workspaceId, string kind, Guid id, CancellationToken ct)
    {
        if (kind == AutomationKinds.Sync)
            return await db.PluggySyncRuns.AsNoTracking().Where(r => r.WorkspaceId == workspaceId && r.Id == id).SingleOrDefaultAsync(ct) is { } sync ? ToRow(sync) : null;
        return await db.PluggyCategorizationRuns.AsNoTracking().Where(r => r.WorkspaceId == workspaceId && r.Id == id).SingleOrDefaultAsync(ct) is { } cat ? ToRow(cat) : null;
    }

    public async Task<AutomationRunRow?> LastRunAsync(Guid workspaceId, string kind, CancellationToken ct)
    {
        if (kind == AutomationKinds.Sync)
            return await db.PluggySyncRuns.AsNoTracking().Where(r => r.WorkspaceId == workspaceId).OrderByDescending(r => r.StartedAt).FirstOrDefaultAsync(ct) is { } sync ? ToRow(sync) : null;
        return await db.PluggyCategorizationRuns.AsNoTracking().Where(r => r.WorkspaceId == workspaceId).OrderByDescending(r => r.StartedAt).FirstOrDefaultAsync(ct) is { } cat ? ToRow(cat) : null;
    }

    public Task<bool> AnyRunningAsync(Guid workspaceId, string kind, DateTimeOffset staleBefore, CancellationToken ct) => kind == AutomationKinds.Sync
        ? db.PluggySyncRuns.AsNoTracking().AnyAsync(r => r.WorkspaceId == workspaceId && r.Status == PluggySyncStatus.Running && r.StartedAt >= staleBefore, ct)
        : db.PluggyCategorizationRuns.AsNoTracking().AnyAsync(r => r.WorkspaceId == workspaceId && r.State == PluggyCategorizationRunState.Running && r.StartedAt >= staleBefore, ct);

    private static DateTimeOffset StartOfDay(DateOnly day) =>
        new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified), Zone), TimeSpan.Zero);

    private static AutomationRunRow ToRow(PluggySyncRun r) => new(r.Id, AutomationKinds.Sync, r.Trigger.ToString(), AutomationKinds.StateOfSync(r.Status), r.StartedAt, r.FinishedAt, r.ConnectionId, null,
        r.Status is PluggySyncStatus.Completed ? null : FirstCode(r.ErrorSummary),
        new AutomationSyncCounts(r.AccountsTotal, r.AccountsSynced, r.Seen, r.Created, r.Updated, r.Unchanged, r.Missing, r.Errors, r.ErrorSummary), null);

    private static AutomationRunRow ToRow(PluggyCategorizationRun r) => new(r.Id, AutomationKinds.Categorization, r.Trigger.ToString(), r.State.ToString(), r.StartedAt, r.FinishedAt, r.ConnectionId, r.SyncRunId,
        r.FailureCode ?? r.AiStoppedBecause, null,
        new AutomationCategorizationCounts(r.Considered, r.ClassifiedLocal, r.ClassifiedAi, r.Pending, r.SkippedChanged, r.AiItemsSent, r.AiCalls, r.UsedAi, r.Model, r.AiStoppedBecause));

    private static string? FirstCode(string? summary) => string.IsNullOrWhiteSpace(summary) ? null : summary.Split(';')[0].Trim().Split(':')[0];
}
