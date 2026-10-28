using Microsoft.EntityFrameworkCore;
using Npgsql;
using Saldo360.Application.Pluggy;
using Saldo360.Domain.Pluggy;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Pluggy;

internal sealed class PluggyImportRepository(Saldo360DbContext db) : IPluggyImportRepository
{
    private const int LookupChunk = 1000;

    public async Task<PluggySyncRun?> TryStartRunAsync(Guid workspaceId, Guid connectionId, Guid userId, DateTimeOffset now, TimeSpan staleAfter, CancellationToken ct)
    {
        var staleBefore = now - staleAfter;
        // Coleta abandonada (processo encerrado no meio) não deve bloquear a conexão para sempre.
        await db.PluggySyncRuns.Where(r => r.ConnectionId == connectionId && r.Status == PluggySyncStatus.Running && r.StartedAt < staleBefore)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, PluggySyncStatus.Failed).SetProperty(r => r.FinishedAt, now)
                .SetProperty(r => r.Errors, r => r.Errors + 1).SetProperty(r => r.ErrorSummary, "Abandoned"), ct);
        var run = PluggySyncRun.Start(workspaceId, connectionId, userId, now);
        db.PluggySyncRuns.Add(run);
        try { await db.SaveChangesAsync(ct); return run; }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.Entry(run).State = EntityState.Detached;
            return null;
        }
    }

    public async Task SaveRunAsync(PluggySyncRun run, CancellationToken ct)
    {
        if (db.Entry(run).State == EntityState.Detached) db.PluggySyncRuns.Update(run);
        await db.SaveChangesAsync(ct);
        db.Entry(run).State = EntityState.Detached;
    }

    public async Task<PluggyAccountApply> ApplyAccountAsync(Guid workspaceId, Guid connectionId, string accountId, IReadOnlyList<PluggyTransactionSnapshot> snapshots,
        bool complete, Guid runId, DateTimeOffset now, CancellationToken ct)
    {
        var result = new PluggyAccountApply();
        // O mesmo ID repetido na coleta vale uma vez (a última ocorrência); a repetição é reportada.
        var byId = new Dictionary<string, PluggyTransactionSnapshot>(StringComparer.Ordinal);
        foreach (var snapshot in snapshots) byId[snapshot.PluggyTransactionId] = snapshot;
        result.DuplicatePayloadIds = snapshots.Count - byId.Count;
        result.Seen = byId.Count;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var existing = new Dictionary<string, PluggyImportedTransaction>(StringComparer.Ordinal);
        foreach (var chunk in byId.Keys.Chunk(LookupChunk))
        {
            var found = await db.PluggyImportedTransactions
                .Where(t => t.ConnectionId == connectionId && t.PluggyAccountId == accountId && chunk.Contains(t.PluggyTransactionId)).ToListAsync(ct);
            foreach (var item in found) existing[item.PluggyTransactionId] = item;
        }

        foreach (var (id, snapshot) in byId)
        {
            if (!existing.TryGetValue(id, out var current))
            {
                db.PluggyImportedTransactions.Add(PluggyImportedTransaction.Create(workspaceId, connectionId, snapshot with { PluggyAccountId = accountId }, runId, now));
                result.New++;
                continue;
            }
            var observation = current.Observe(snapshot with { PluggyAccountId = accountId }, runId, now);
            if (observation.Changed)
            {
                db.PluggyTransactionChanges.Add(PluggyTransactionChange.Create(current, observation, runId, now));
                result.Updated++;
            }
            else result.Unchanged++;
        }
        await db.SaveChangesAsync(ct);

        if (complete)
        {
            // Ausente de uma coleta completa: apenas contabiliza. Nada é excluído nem alterado.
            result.Missing = await db.PluggyImportedTransactions.Where(t => t.ConnectionId == connectionId && t.PluggyAccountId == accountId && t.LastSeenRunId != runId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.MissingSyncCount, t => t.MissingSyncCount + 1).SetProperty(t => t.FirstMissingAt, t => t.FirstMissingAt ?? now), ct);
        }
        await transaction.CommitAsync(ct);
        foreach (var entry in db.ChangeTracker.Entries().Where(e => e.Entity is PluggyImportedTransaction or PluggyTransactionChange).ToList()) entry.State = EntityState.Detached;
        return result;
    }

    public async Task<PluggyImportedPage> ListAsync(Guid workspaceId, PluggyImportFilter f, DateTimeOffset now, CancellationToken ct)
    {
        var scope = db.PluggyImportedTransactions.AsNoTracking().Where(t => t.WorkspaceId == workspaceId);
        if (f.ConnectionId is { } connection) scope = scope.Where(t => t.ConnectionId == connection);
        if (!string.IsNullOrWhiteSpace(f.AccountId)) scope = scope.Where(t => t.PluggyAccountId == f.AccountId);
        if (f.From is { } from) { var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero); scope = scope.Where(t => t.Date >= start); }
        if (f.To is { } to) { var end = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero); scope = scope.Where(t => t.Date < end); }

        // "Última sincronização" de cada conexão: a coleta concluída mais recente. Poucas linhas; resolvidas em memória.
        var runs = await db.PluggySyncRuns.AsNoTracking().Where(r => r.WorkspaceId == workspaceId && r.Status != PluggySyncStatus.Running)
            .Select(r => new { r.Id, r.ConnectionId, r.StartedAt }).ToListAsync(ct);
        var latestByConnection = runs.GroupBy(r => r.ConnectionId).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.StartedAt).First());
        var latestIds = latestByConnection.Values.Select(r => r.Id).ToList();
        var scopedLatest = f.ConnectionId is { } c ? latestByConnection.Where(p => p.Key == c).Select(p => p.Value).ToList() : latestByConnection.Values.ToList();

        var counters = new PluggyImportCounters(
            await scope.CountAsync(ct),
            await scope.CountAsync(t => latestIds.Contains(t.FirstSeenRunId), ct),
            await scope.CountAsync(t => t.LastChangedRunId != null && latestIds.Contains(t.LastChangedRunId.Value) && !latestIds.Contains(t.FirstSeenRunId), ct),
            await scope.CountAsync(t => t.Status == PluggyTransactionStatus.Pending, ct),
            await scope.CountAsync(t => t.MissingSyncCount > 0, ct),
            scopedLatest.Count == 0 ? null : scopedLatest.Max(r => r.StartedAt));

        var filtered = scope;
        if (f.Status is { } status) filtered = filtered.Where(t => t.Status == status);
        if (f.Classification is { } classification) filtered = filtered.Where(t => t.Classification == classification);
        var total = await filtered.CountAsync(ct);
        var rows = await filtered.OrderByDescending(t => t.Date).ThenBy(t => t.Id).Skip((f.Page - 1) * f.PageSize).Take(f.PageSize).ToListAsync(ct);

        var keys = rows.Where(r => r.PossibleDuplicateKey != null).Select(r => r.PossibleDuplicateKey!).Distinct().ToList();
        var duplicates = keys.Count == 0 ? [] : await db.PluggyImportedTransactions.AsNoTracking().Where(t => t.WorkspaceId == workspaceId && keys.Contains(t.PossibleDuplicateKey!))
            .GroupBy(t => t.PossibleDuplicateKey!).Select(g => new { Key = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var items = rows.Select(t => new PluggyImportedTransactionView(t.Id, t.ConnectionId, t.PluggyAccountId, t.AccountType, t.AccountLabel, t.PluggyTransactionId, t.Date,
            t.Description, t.DescriptionRaw, Money(t.Amount), t.Currency, t.Type, t.Status.ToString(), t.OperationType, t.Category, t.InstallmentNumber, t.TotalInstallments,
            t.BillId, t.CardLast4, t.Classification.ToString(), t.ClassificationReason, t.FirstImportedAt, t.LastObservedAt, t.LastChangedAt, t.MissingSyncCount,
            t.PossibleDuplicateKey is null ? null : t.PossibleDuplicateKey[..12], t.PossibleDuplicateKey is not null && duplicates.GetValueOrDefault(t.PossibleDuplicateKey) > 1 ? duplicates[t.PossibleDuplicateKey] : 1,
            latestIds.Contains(t.FirstSeenRunId), t.LastChangedRunId is { } changed && latestIds.Contains(changed) && !latestIds.Contains(t.FirstSeenRunId))).ToArray();
        return new(items, total, f.Page, f.PageSize, counters);
    }

    public async Task<IReadOnlyList<PluggySyncRun>> ListRunsAsync(Guid workspaceId, Guid connectionId, int take, CancellationToken ct) =>
        await db.PluggySyncRuns.AsNoTracking().Where(r => r.WorkspaceId == workspaceId && r.ConnectionId == connectionId).OrderByDescending(r => r.StartedAt).Take(take).ToListAsync(ct);

    public async Task<PluggyStabilityView> GetStabilityAsync(Guid workspaceId, Guid connectionId, DateTimeOffset now, CancellationToken ct)
    {
        var tx = db.PluggyImportedTransactions.AsNoTracking().Where(t => t.WorkspaceId == workspaceId && t.ConnectionId == connectionId);
        var finished = await db.PluggySyncRuns.AsNoTracking().Where(r => r.WorkspaceId == workspaceId && r.ConnectionId == connectionId
            && (r.Status == PluggySyncStatus.Completed || r.Status == PluggySyncStatus.CompletedWithErrors)).OrderByDescending(r => r.StartedAt).Take(2).ToListAsync(ct);
        var completedRuns = await db.PluggySyncRuns.CountAsync(r => r.WorkspaceId == workspaceId && r.ConnectionId == connectionId
            && (r.Status == PluggySyncStatus.Completed || r.Status == PluggySyncStatus.CompletedWithErrors), ct);
        var latest = finished.ElementAtOrDefault(0);
        var previous = finished.ElementAtOrDefault(1);

        var changes = await db.PluggyTransactionChanges.AsNoTracking().Where(c => c.WorkspaceId == workspaceId && tx.Any(t => t.Id == c.TransactionId))
            .Select(c => new { c.SyncRunId, c.ChangedFields, c.PreviousStatus, c.NewStatus }).ToListAsync(ct);
        static bool Transition(PluggyTransactionStatus from, PluggyTransactionStatus to) => from == PluggyTransactionStatus.Pending && to == PluggyTransactionStatus.Posted;
        var latestChanges = latest is null ? [] : changes.Where(c => c.SyncRunId == latest.Id).ToList();
        var fieldCounts = latestChanges.SelectMany(c => c.ChangedFields.Split(',', StringSplitOptions.RemoveEmptyEntries)).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());

        int previouslyImported = 0, preserved = 0, newInLatest = 0, late = 0;
        if (latest is not null)
        {
            previouslyImported = await tx.CountAsync(t => t.FirstSeenRunId != latest.Id && t.FirstImportedAt < latest.StartedAt, ct);
            preserved = await tx.CountAsync(t => t.FirstSeenRunId != latest.Id && t.FirstImportedAt < latest.StartedAt && t.LastSeenRunId == latest.Id, ct);
            newInLatest = await tx.CountAsync(t => t.FirstSeenRunId == latest.Id, ct);
            if (previous is not null) late = await tx.CountAsync(t => t.FirstSeenRunId == latest.Id && t.Date != null && t.Date < previous.StartedAt, ct);
        }
        var duplicateGroups = await tx.Where(t => t.PossibleDuplicateKey != null).GroupBy(t => t.PossibleDuplicateKey).CountAsync(g => g.Count() > 1, ct);
        var notes = new List<string>();
        if (completedRuns < 2) notes.Add("São necessárias ao menos duas sincronizações concluídas para comparar estabilidade.");
        notes.Add("\"Não vistas\" conta só transações que a última coleta não trouxe; coletas incompletas de alguma conta (erros) inflam esse número.");
        notes.Add("Nenhuma transação é excluída por ausência; MissingSyncCount apenas acumula.");

        return new(connectionId, completedRuns, previous?.Id, latest?.Id, previous?.StartedAt, latest?.StartedAt, previouslyImported, preserved, previouslyImported - preserved,
            newInLatest, late, fieldCounts, latestChanges.Count(c => Transition(c.PreviousStatus, c.NewStatus)),
            latestChanges.Count(c => c.PreviousStatus != c.NewStatus && !Transition(c.PreviousStatus, c.NewStatus)),
            changes.Count(c => Transition(c.PreviousStatus, c.NewStatus)),
            changes.Count(c => c.ChangedFields.Split(',').Contains("amount")), changes.Count(c => c.ChangedFields.Split(',').Contains("description")),
            await tx.CountAsync(t => t.Date > now, ct), await tx.CountAsync(t => t.Date > now && t.Status == PluggyTransactionStatus.Pending, ct),
            await tx.CountAsync(t => t.MissingSyncCount > 0, ct), await tx.CountAsync(t => t.Status == PluggyTransactionStatus.Pending, ct), duplicateGroups, notes);
    }

    private static string? Money(decimal? value) => value?.ToString("0.00##", System.Globalization.CultureInfo.InvariantCulture);
}
