using Microsoft.EntityFrameworkCore;
using Npgsql;
using Saldo360.Application.Pluggy;
using Saldo360.Domain.Pluggy;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Pluggy;

/// <summary>
/// Estado de monitoramento e agendamento das conexões (T09.6). A reserva (<c>ClaimedUntil</c>) é atômica no banco: duas instâncias nunca tratam a mesma conexão
/// ao mesmo tempo; e, por baixo, o índice único de execução "em andamento" garante que nunca haja duas coletas simultâneas da mesma conexão.
/// </summary>
internal sealed class PluggySyncStateRepository(Saldo360DbContext db) : IPluggySyncStateRepository
{
    public Task<PluggySyncState?> FindAsync(Guid workspaceId, Guid connectionId, CancellationToken ct) =>
        db.PluggySyncStates.AsNoTracking().SingleOrDefaultAsync(s => s.WorkspaceId == workspaceId && s.ConnectionId == connectionId, ct);

    public async Task<IReadOnlyList<PluggySyncState>> ListAsync(Guid workspaceId, CancellationToken ct) =>
        await db.PluggySyncStates.AsNoTracking().Where(s => s.WorkspaceId == workspaceId).ToListAsync(ct);

    public async Task<IReadOnlyList<PluggySyncCandidate>> ListDueAsync(DateTimeOffset now, int take, CancellationToken ct)
    {
        var rows = await (from link in db.PluggyItemLinks.AsNoTracking()
                          join workspace in db.FinancialWorkspaces.AsNoTracking() on link.WorkspaceId equals workspace.Id
                          where workspace.IsActive && db.PluggyAccountLinks.Any(account => account.ConnectionId == link.Id && account.WorkspaceId == link.WorkspaceId)
                          join state in db.PluggySyncStates.AsNoTracking() on link.Id equals state.ConnectionId into states
                          from state in states.DefaultIfEmpty()
                          // Orçamento com a automação pausada pelo usuário (R04): nenhuma execução agendada nova. "Sincronizar agora" continua valendo.
                          where !db.PluggyAutomationSettings.Any(settings => settings.WorkspaceId == link.WorkspaceId && settings.SyncPaused)
                          where state == null || ((state.NextAttemptAt == null || state.NextAttemptAt <= now) && (state.ClaimedUntil == null || state.ClaimedUntil <= now))
                          orderby state.NextAttemptAt, link.CreatedAt
                          select new { link, state }).Take(take).ToListAsync(ct);
        return rows.Select(row => new PluggySyncCandidate(row.link, row.state)).ToList();
    }

    public async Task<bool> TryClaimAsync(Guid workspaceId, Guid connectionId, DateTimeOffset now, DateTimeOffset until, CancellationToken ct)
    {
        await EnsureAsync(workspaceId, connectionId, now, ct);
        var claimed = await db.PluggySyncStates.Where(s => s.WorkspaceId == workspaceId && s.ConnectionId == connectionId
                && (s.ClaimedUntil == null || s.ClaimedUntil <= now) && (s.NextAttemptAt == null || s.NextAttemptAt <= now))
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.ClaimedUntil, until).SetProperty(s => s.UpdatedAt, now), ct);
        return claimed == 1;
    }

    public async Task SaveAsync(Guid workspaceId, Guid connectionId, Action<PluggySyncState> change, DateTimeOffset now, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await EnsureAsync(workspaceId, connectionId, now, ct);
            var state = await db.PluggySyncStates.SingleAsync(s => s.WorkspaceId == workspaceId && s.ConnectionId == connectionId, ct);
            change(state);
            try { await db.SaveChangesAsync(ct); db.Entry(state).State = EntityState.Detached; return; }
            catch (DbUpdateConcurrencyException) when (attempt < 2) { db.Entry(state).State = EntityState.Detached; }
        }
    }

    public async Task<int> RecoverAbandonedAsync(DateTimeOffset now, TimeSpan staleAfter, CancellationToken ct)
    {
        var staleBefore = now - staleAfter;
        var abandoned = await db.PluggySyncRuns.AsNoTracking().Where(r => r.Status == PluggySyncStatus.Running && r.StartedAt < staleBefore)
            .Select(r => new { r.Id, r.WorkspaceId, r.ConnectionId, r.Trigger }).ToListAsync(ct);
        if (abandoned.Count == 0) return 0;
        var ids = abandoned.Select(r => r.Id).ToList();
        await db.PluggySyncRuns.Where(r => ids.Contains(r.Id) && r.Status == PluggySyncStatus.Running)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.Status, PluggySyncStatus.Failed).SetProperty(r => r.FinishedAt, now)
                .SetProperty(r => r.Errors, r => r.Errors + 1).SetProperty(r => r.ErrorSummary, "Abandoned"), ct);
        // A conexão também passa a constar como falha (com nova tentativa), para o monitoramento não mostrar uma execução eterna.
        foreach (var run in abandoned)
            await SaveAsync(run.WorkspaceId, run.ConnectionId, state => state.RecordAttempt(now, PluggySyncOutcome.Failed, run.Trigger, run.Id, "Abandoned", true, now, null), now, ct);
        return abandoned.Count;
    }

    public async Task<IReadOnlyDictionary<Guid, PluggyConnectionAccounts>> AccountsByConnectionAsync(Guid workspaceId, CancellationToken ct) =>
        (await db.PluggyAccountLinks.AsNoTracking().Where(l => l.WorkspaceId == workspaceId).GroupBy(l => l.ConnectionId)
            .Select(g => new { g.Key, Count = g.Count(), Institution = g.Select(l => l.InstitutionName).FirstOrDefault() }).ToListAsync(ct))
        .ToDictionary(x => x.Key, x => new PluggyConnectionAccounts(x.Count, x.Institution));

    private async Task EnsureAsync(Guid workspaceId, Guid connectionId, DateTimeOffset now, CancellationToken ct)
    {
        if (await db.PluggySyncStates.AnyAsync(s => s.ConnectionId == connectionId, ct)) return;
        var created = PluggySyncState.Create(workspaceId, connectionId, now);
        db.PluggySyncStates.Add(created);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Outra instância criou a linha primeiro: basta usá-la.
        }
        db.Entry(created).State = EntityState.Detached;
    }
}
