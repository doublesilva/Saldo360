using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.Workspaces;
using Saldo360.Infrastructure.CreditCards;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Workspaces;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DocumentCutoverEntry(Guid DocumentId, Guid WorkspaceId, string SourceStorageReference,
    string TargetStorageReference, long SourceLength, string SourceSha256);
public sealed record DocumentCutoverResult(string Status, int Migrated, int AlreadyMigrated, bool OriginalsPreserved);

/// <summary>Offline maintenance only. Not registered in API DI and never called by reset.</summary>
public sealed class FinancialDocumentWorkspaceCutover
{
    private readonly Saldo360DbContext db;
    private readonly FileSystemFinancialDocumentStorage storage;
    public FinancialDocumentWorkspaceCutover(Saldo360DbContext db, string existingStorageRoot)
    { this.db = db; storage = FileSystemFinancialDocumentStorage.ForMaintenance(existingStorageRoot); }

    private sealed record Row(Guid Id, Guid WorkspaceId, string Reference, string Hash);
    private static string Target(Guid w, Guid id) => $"workspaces/{w:N}/{id:N}.pdf";
    private static void Require(bool condition, string code)
    { if (!condition) throw new InvalidOperationException(code); }
    private async Task<List<Row>> Rows(Guid w, int expected, CancellationToken ct)
    {
        Require(w != Guid.Empty && expected > 0, "ExplicitWorkspaceAndCountRequired");
        Require(await db.FinancialWorkspaces.AsNoTracking().AnyAsync(x => x.Id == w && x.Type == FinancialWorkspaceType.Personal && x.IsActive, ct), "ActivePersonalRequired");
        var rows = await db.FinancialDocumentInboxItems.AsNoTracking().Where(x => x.WorkspaceId == w)
            .OrderBy(x => x.Id).Select(x => new Row(x.Id, x.WorkspaceId, x.StorageReference, x.ContentHash)).ToListAsync(ct);
        Require(rows.Count == expected, "UnexpectedDocumentCount");
        Require(rows.Select(x => x.Reference).Distinct(StringComparer.Ordinal).Count() == expected, "AmbiguousReference");
        return rows;
    }
    private void ValidateReference(Guid w, Guid id, string source, string target)
    {
        Require(target == Target(w, id), "UnexpectedTargetNamespace");
        storage.ResolveForMaintenance(source); storage.ResolveForMaintenance(target);
        var parts = source.Split('/');
        Require(source == target || parts.Length == 2 && Guid.TryParseExact(parts[0], "N", out _)
            && parts[1] == $"{id:N}.pdf", "UnexpectedSourceNamespace");
    }
    private async Task<(long Length, string Hash)> Inspect(string reference, CancellationToken ct)
    {
        var path = storage.ResolveForMaintenance(reference);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        var length = input.Length;
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct));
        Require(input.Length == length, "FileChangedDuringRead");
        return (length, hash);
    }
    private async Task Verify(string reference, DocumentCutoverEntry item, CancellationToken ct)
    {
        var actual = await Inspect(reference, ct);
        Require(actual.Length == item.SourceLength && actual.Hash == item.SourceSha256, "FileFingerprintMismatch");
    }
    private bool Exists(string reference)
    {
        var path = storage.ResolveForMaintenance(reference);
        Require(!Directory.Exists(path), "FileReferenceIsDirectory");
        return File.Exists(path);
    }

    public async Task<IReadOnlyList<DocumentCutoverEntry>> DryRunAsync(Guid workspaceId, int expectedCount, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
        var rows = await Rows(workspaceId, expectedCount, ct);
        var plan = new List<DocumentCutoverEntry>();
        foreach (var row in rows)
        {
            var target = Target(workspaceId, row.Id);
            ValidateReference(workspaceId, row.Id, row.Reference, target);
            var source = await Inspect(row.Reference, ct);
            Require(source.Hash == row.Hash.ToLowerInvariant(), "StoredContentHashMismatch");
            var entry = new DocumentCutoverEntry(row.Id, workspaceId, row.Reference, target, source.Length, source.Hash);
            if (Exists(target)) await Verify(target, entry, ct);
            plan.Add(entry);
        }
        await tx.CommitAsync(ct);
        return plan;
    }

    public async Task<DocumentCutoverResult> ApplyAsync(Guid workspaceId, int expectedCount,
        IReadOnlyList<DocumentCutoverEntry> plan, bool maintenanceWindowConfirmed, CancellationToken ct)
    {
        Require(maintenanceWindowConfirmed, "MaintenanceWindowApprovalRequired");
        Require(plan.Count == expectedCount && plan.Select(x => x.DocumentId).Distinct().Count() == expectedCount, "UnexpectedPlanCount");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Same PostgreSQL root lock as financial writers/reset; hold through copy and reference commit.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM financial_workspaces WHERE id={workspaceId} FOR UPDATE", ct);
        var rows = await Rows(workspaceId, expectedCount, ct);
        var states = new Dictionary<Guid, bool>();
        foreach (var item in plan)
        {
            Require(item.WorkspaceId == workspaceId && item.SourceLength >= 0 && item.SourceSha256 is { Length: 64 }
                && item.SourceSha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'), "InvalidPlanEntry");
            ValidateReference(workspaceId, item.DocumentId, item.SourceStorageReference, item.TargetStorageReference);
            var row = rows.SingleOrDefault(x => x.Id == item.DocumentId);
            Require(row is not null && row.Hash.ToLowerInvariant() == item.SourceSha256, "DocumentIdentityMismatch");
            Require(row!.Reference == item.SourceStorageReference || row.Reference == item.TargetStorageReference, "StorageReferenceChanged");
            Require(!await db.FinancialDocumentInboxItems.AsNoTracking().AnyAsync(x => x.Id != item.DocumentId
                && (x.StorageReference == item.SourceStorageReference || x.StorageReference == item.TargetStorageReference), ct), "AmbiguousReference");
            var migrated = row.Reference == item.TargetStorageReference;
            states[item.DocumentId] = migrated;
            if (migrated) await Verify(item.TargetStorageReference, item, ct);
            else
            {
                await Verify(item.SourceStorageReference, item, ct);
                if (Exists(item.TargetStorageReference)) await Verify(item.TargetStorageReference, item, ct);
            }
        }
        // All preconditions above pass for the whole plan before the first filesystem write.
        foreach (var item in plan.Where(x => !states[x.DocumentId])) await Copy(item, ct);
        var originalsPreserved = true;
        foreach (var item in plan)
        {
            if (!states[item.DocumentId] || Exists(item.SourceStorageReference)) await Verify(item.SourceStorageReference, item, ct);
            else originalsPreserved = false; // AlreadyMigrated can outlive a separately approved legacy cleanup.
            await Verify(item.TargetStorageReference, item, ct);
            storage.FlushMaintenanceDirectories(item.TargetStorageReference);
        }
        foreach (var item in plan.Where(x => !states[x.DocumentId]))
        {
            var changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE financial_document_inbox_items SET storage_reference={item.TargetStorageReference}
                WHERE id={item.DocumentId} AND workspace_id={workspaceId} AND storage_reference={item.SourceStorageReference}
                """, ct);
            Require(changed == 1, "StorageReferenceChanged");
        }
        // Read persisted references and bytes through the application's storage implementation before commit.
        var final = await Rows(workspaceId, expectedCount, ct);
        foreach (var item in plan)
        {
            Require(final.Single(x => x.Id == item.DocumentId).Reference == item.TargetStorageReference, "ReferenceVerificationFailed");
            await using var read = await storage.OpenReadAsync(item.TargetStorageReference, ct);
            Require(read is not null && read.Length == item.SourceLength, "ApplicationReadFailed");
            Require(Convert.ToHexStringLower(await SHA256.HashDataAsync(read!, ct)) == item.SourceSha256, "ApplicationReadFailed");
        }
        await tx.CommitAsync(ct);
        return new(states.Values.All(x => x) ? "AlreadyMigrated" : "Migrated", states.Values.Count(x => !x), states.Values.Count(x => x), originalsPreserved);
    }

    private async Task Copy(DocumentCutoverEntry item, CancellationToken ct)
    {
        if (Exists(item.TargetStorageReference)) { await Verify(item.TargetStorageReference, item, ct); return; }
        var source = storage.ResolveForMaintenance(item.SourceStorageReference);
        var target = storage.ResolveForMaintenance(item.TargetStorageReference);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        storage.ResolveForMaintenance(item.TargetStorageReference);
        var temporaryReference = item.TargetStorageReference + "." + Guid.NewGuid().ToString("N") + ".cutover.tmp";
        var temporary = storage.ResolveForMaintenance(temporaryReference);
        var writeOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            BufferSize = 81920, Options = FileOptions.Asynchronous | FileOptions.WriteThrough };
        if (!OperatingSystem.IsWindows()) writeOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        var ownsTemporary = false;
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous))
            await using (var output = new FileStream(temporary, writeOptions))
            {
                ownsTemporary = true;
                await input.CopyToAsync(output, ct); await output.FlushAsync(ct); output.Flush(flushToDisk: true);
            }
            await Verify(temporaryReference, item, ct);
            storage.ResolveForMaintenance(item.TargetStorageReference);
            try { File.Move(temporary, target, overwrite: false); ownsTemporary = false; }
            catch (IOException) when (Exists(item.TargetStorageReference)) { await Verify(item.TargetStorageReference, item, ct); }
        }
        finally { if (ownsTemporary && File.Exists(temporary)) File.Delete(temporary); } // Only our temporary copy, never a preexisting file or source/target.
    }
}
