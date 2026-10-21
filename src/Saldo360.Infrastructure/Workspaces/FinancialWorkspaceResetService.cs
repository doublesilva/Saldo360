using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Saldo360.Application.CreditCards;
using Saldo360.Application.Workspaces;
using Saldo360.Domain.Workspaces;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Workspaces;

internal sealed class FinancialWorkspaceResetService(Saldo360DbContext db, IFinancialDocumentStorage storage) : IFinancialWorkspaceResetService
{
    private sealed record Content(IEntityType Entity, string Name, string SqlTable);
    private List<Content> Inventory()
    {
        var names = typeof(Saldo360DbContext).GetProperties()
            .Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
            .ToDictionary(p => p.PropertyType.GenericTypeArguments[0], p => p.Name);
        var sql = db.GetService<Microsoft.EntityFrameworkCore.Storage.ISqlGenerationHelper>();
        var remaining = db.Model.GetEntityTypes().Where(e => e.FindProperty("WorkspaceId") is not null
            && e.ClrType != typeof(FinancialResetAudit) && e.ClrType != typeof(FinancialResetFileCleanup)).ToList();
        var ordered = new List<Content>();
        while (remaining.Count != 0)
        {
            var leaf = remaining.FirstOrDefault(parent => !remaining.Any(child => child != parent && child.GetForeignKeys().Any(f => f.PrincipalEntityType == parent)));
            if (leaf is null) throw new InvalidOperationException("Reset inventory contains a dependency cycle.");
            var name = names[leaf.ClrType];
            if (name.StartsWith("Financial", StringComparison.Ordinal)) name = name[9..];
            ordered.Add(new(leaf, JsonNamingPolicy.CamelCase.ConvertName(name), sql.DelimitIdentifier(leaf.GetTableName()!, leaf.GetSchema() ?? "public")));
            remaining.Remove(leaf);
        }
        return ordered;
    }
    private async Task Authorize(Guid w, Guid actor, CancellationToken ct)
    {
        var root = await db.FinancialWorkspaces.AsNoTracking().SingleOrDefaultAsync(x => x.Id == w && x.OwnerUserId == actor, ct);
        if (root is null) throw new FinancialResetRejected("NotFound");
        if (root.Type != FinancialWorkspaceType.Personal) throw new FinancialResetRejected("PersonalOnly");
        if (!root.IsActive) throw new FinancialResetRejected("WorkspaceInactive");
    }
    private async Task<long> Execute(string sql, Guid w, bool scalar, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction(); command.CommandText = sql;
            var parameter = command.CreateParameter(); parameter.ParameterName = "w"; parameter.Value = w; command.Parameters.Add(parameter);
            return scalar ? Convert.ToInt64(await command.ExecuteScalarAsync(ct)) : await command.ExecuteNonQueryAsync(ct);
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }
    public async Task<FinancialResetReport> PreviewAsync(Guid w, Guid actor, CancellationToken ct)
    {
        await Authorize(w, actor, ct);
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await Authorize(w, actor, ct);
        var counts = new Dictionary<string, long>();
        foreach (var item in Inventory()) counts[item.Name] = await Execute($"SELECT count(*) FROM {item.SqlTable} WHERE workspace_id=@w", w, true, ct);
        var pending = await db.Set<FinancialResetFileCleanup>().LongCountAsync(x => x.WorkspaceId == w, ct);
        await tx.CommitAsync(ct);
        return new(w, "Ready", counts, pending);
    }
    public async Task<FinancialResetReport> ResetAsync(Guid w, Guid actor, string confirmation, string traceId, CancellationToken ct)
    {
        if (confirmation != "APAGAR ORÇAMENTO") throw new FinancialResetRejected("InvalidConfirmation");
        await Authorize(w, actor, ct);
        var deleted = new Dictionary<string, long>();
        long pendingBeforeCommit;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await Execute("SELECT id FROM financial_workspaces WHERE id=@w FOR UPDATE", w, false, ct);
            await Authorize(w, actor, ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('saldo360.resetting_workspace', {w.ToString()}, true)", ct);
            var invalid = await Execute("SELECT count(*) FROM financial_document_inbox_items WHERE workspace_id=@w AND storage_reference <> 'workspaces/' || replace(workspace_id::text,'-','') || '/' || replace(id::text,'-','') || '.pdf'", w, true, ct);
            if (invalid != 0) throw new FinancialResetRejected("DocumentCutoverRequired");
            await Execute("INSERT INTO financial_workspace_reset_file_cleanup (id,workspace_id,storage_reference,created_at,attempts) SELECT gen_random_uuid(),workspace_id,storage_reference,clock_timestamp(),0 FROM financial_document_inbox_items WHERE workspace_id=@w ON CONFLICT (workspace_id,storage_reference) DO NOTHING", w, false, ct);
            foreach (var item in Inventory()) deleted[item.Name] = await Execute($"DELETE FROM {item.SqlTable} WHERE workspace_id=@w", w, false, ct);
            await Execute("UPDATE financial_workspaces SET last_financial_reset_at=clock_timestamp() WHERE id=@w", w, false, ct);
            db.Set<FinancialResetAudit>().Add(new() { Id = Guid.CreateVersion7(), WorkspaceId = w, ActorUserId = actor,
                CreatedAt = DateTimeOffset.UtcNow, TraceId = traceId.Length > 128 ? traceId[..128] : traceId, DeletedEntities = deleted.Values.Sum() });
            await db.SaveChangesAsync(ct);
            pendingBeforeCommit = await db.Set<FinancialResetFileCleanup>().LongCountAsync(x => x.WorkspaceId == w, ct);
            await tx.CommitAsync(ct);
        }
        long pending;
        try { pending = await RetryCleanupAsync(w, actor, CancellationToken.None); }
        catch (Exception error) when (error is Npgsql.NpgsqlException or IOException or TimeoutException) { pending = pendingBeforeCommit; }
        return new(w, deleted.Values.Any(n => n != 0) ? "ResetCompleted" : "AlreadyEmpty", deleted, pending);
    }
    public async Task<long> RetryCleanupAsync(Guid w, Guid actor, CancellationToken ct)
    {
        await Authorize(w, actor, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await Execute("SELECT id FROM financial_workspaces WHERE id=@w FOR UPDATE", w, false, ct);
        await Authorize(w, actor, ct);
        var queued = await db.Set<FinancialResetFileCleanup>().Where(x => x.WorkspaceId == w).OrderBy(x => x.Attempts).ThenBy(x => x.CreatedAt).Take(100).ToListAsync(ct);
        foreach (var item in queued)
        {
            var suffix = item.StorageReference.Split('/');
            if (suffix.Length != 3 || suffix[0] != "workspaces" || suffix[1] != w.ToString("N") || !suffix[2].EndsWith(".pdf", StringComparison.Ordinal)
                || !Guid.TryParseExact(suffix[2][..^4], "N", out _)) continue;
            if (await db.FinancialDocumentInboxItems.AnyAsync(x => x.StorageReference == item.StorageReference, ct)) continue;
            try { await storage.DeleteAsync(item.StorageReference, ct); db.Set<FinancialResetFileCleanup>().Remove(item); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { item.Attempts++; }
        }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return await db.Set<FinancialResetFileCleanup>().LongCountAsync(x => x.WorkspaceId == w, ct);
    }
}
