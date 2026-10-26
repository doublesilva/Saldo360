using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Saldo360.Domain.CreditCards;
using Saldo360.Infrastructure.CreditCards;
using Saldo360.Infrastructure.Workspaces;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Persistence;

[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "INFRA")]
[Trait("Area", "Infrastructure")]
[Trait("Area", "Documents")]
[Trait("Area", "Workspaces")]
[Trait("Dependency", "PostgreSQL")]
public sealed class FinancialDocumentWorkspaceCutoverTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private sealed class Files : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "saldo360-document-cutover-" + Guid.NewGuid().ToString("N"));
        public Files() => Directory.CreateDirectory(Root);
        public string PathFor(string reference) => Path.Combine(Root, reference.Replace('/', Path.DirectorySeparatorChar));
        public async Task Write(string reference, byte[] bytes) { var path = PathFor(reference); Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllBytesAsync(path, bytes); }
        public void Dispose() => Directory.Delete(Root, true); // Only this fixture's GUID directory.
    }
    private async Task<(Guid Workspace, IReadOnlyList<DocumentCutoverEntry> Plan)> Arrange(Files files, int count = 2)
    {
        var session = await SignInAsync("cutover-" + Guid.NewGuid().ToString("N"));
        var w = await CreateWorkspaceAsync(session);
        await using var db = Db();
        for (var i = 0; i < count; i++)
        {
            var id = Guid.NewGuid(); var source = $"{Guid.NewGuid():N}/{id:N}.pdf";
            var bytes = System.Text.Encoding.UTF8.GetBytes("%PDF-synthetic-" + id);
            await files.Write(source, bytes);
            db.Add(FinancialDocumentInboxItem.Create(id, w.Id, FinancialDocumentSource.Manual, null, "synthetic.pdf",
                Convert.ToHexStringLower(SHA256.HashData(bytes)), "application/pdf", source, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }
        await db.SaveChangesAsync();
        return (w.Id, await new FinancialDocumentWorkspaceCutover(db, files.Root).DryRunAsync(w.Id, count, default));
    }
    private async Task<string[]> References(Guid w)
    { await using var db = Db(); return await db.FinancialDocumentInboxItems.Where(x => x.WorkspaceId == w).OrderBy(x => x.Id).Select(x => x.StorageReference).ToArrayAsync(); }
    private async Task<string> WithoutReferences(Guid w)
    {
        await using var db = Db();
        return await db.Database.SqlQuery<string>($"SELECT jsonb_agg(to_jsonb(x)-'storage_reference' ORDER BY id)::text AS \"Value\" FROM financial_document_inbox_items x WHERE workspace_id={w}").SingleAsync();
    }

    [Fact]
    public async Task DryRun_IsReadOnly_AndChecksExpectedCount()
    {
        using var files = new Files(); var (w, plan) = await Arrange(files);
        var before = await References(w); var paths = Directory.GetFiles(files.Root, "*", SearchOption.AllDirectories).Order().ToArray();
        await using var db = Db(); var tool = new FinancialDocumentWorkspaceCutover(db, files.Root);
        var dry = await tool.DryRunAsync(w, 2, default);
        Assert.Equal(plan, dry); Assert.Equal(before, await References(w));
        Assert.Equal(paths, Directory.GetFiles(files.Root, "*", SearchOption.AllDirectories).Order());
        Assert.False(Directory.Exists(files.PathFor("workspaces")));
        Assert.Equal("UnexpectedDocumentCount", (await Assert.ThrowsAsync<InvalidOperationException>(() => tool.DryRunAsync(w, 5, default))).Message);
    }
    [Fact]
    public async Task CopyVerifyUpdate_PreservesOriginalsAllOtherColumnsAndOtherWorkspace_ThenIsIdempotent()
    {
        using var files = new Files(); var (w, plan) = await Arrange(files); var (other, _) = await Arrange(files, 1);
        var otherBefore = await References(other); var metadata = await WithoutReferences(w);
        await using var db = Db(); var tool = new FinancialDocumentWorkspaceCutover(db, files.Root);
        var result = await tool.ApplyAsync(w, 2, plan, true, default);
        Assert.Equal("Migrated", result.Status); Assert.Equal(2, result.Migrated); Assert.True(result.OriginalsPreserved);
        Assert.Equal(metadata, await WithoutReferences(w)); Assert.Equal(otherBefore, await References(other));
        foreach (var item in plan)
        {
            Assert.Equal(await File.ReadAllBytesAsync(files.PathFor(item.SourceStorageReference)), await File.ReadAllBytesAsync(files.PathFor(item.TargetStorageReference)));
            await using var read = await FileSystemFinancialDocumentStorage.ForMaintenance(files.Root).OpenReadAsync(item.TargetStorageReference, default);
            Assert.NotNull(read); Assert.Equal(item.SourceLength, read.Length);
        }
        Assert.Equal("AlreadyMigrated", (await tool.ApplyAsync(w, 2, plan, true, default)).Status);
        Assert.All(await tool.DryRunAsync(w, 2, default), x => Assert.Equal(x.SourceStorageReference, x.TargetStorageReference));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingTarget_IsReusedOnlyWhenFingerprintMatches(bool diverges)
    {
        using var files = new Files(); var (w, plan) = await Arrange(files);
        await files.Write(plan[1].TargetStorageReference, diverges ? "different"u8.ToArray() : await File.ReadAllBytesAsync(files.PathFor(plan[1].SourceStorageReference)));
        await using var db = Db(); var tool = new FinancialDocumentWorkspaceCutover(db, files.Root);
        if (diverges)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => tool.ApplyAsync(w, 2, plan, true, default));
            Assert.False(File.Exists(files.PathFor(plan[0].TargetStorageReference))); Assert.Equal(plan.Select(x => x.SourceStorageReference), await References(w));
        }
        else Assert.Equal("Migrated", (await tool.ApplyAsync(w, 2, plan, true, default)).Status);
    }
    [Fact]
    public async Task MissingSourceOrAlteredBytes_AbortBeforeAnyCopy()
    {
        using var files = new Files(); var (w, plan) = await Arrange(files);
        var source = files.PathFor(plan[1].SourceStorageReference); var original = await File.ReadAllBytesAsync(source); File.Delete(source);
        await using var db = Db(); var tool = new FinancialDocumentWorkspaceCutover(db, files.Root);
        await Assert.ThrowsAsync<FileNotFoundException>(() => tool.ApplyAsync(w, 2, plan, true, default));
        await files.Write(plan[1].SourceStorageReference, "changed"u8.ToArray());
        await Assert.ThrowsAsync<InvalidOperationException>(() => tool.ApplyAsync(w, 2, plan, true, default));
        Assert.False(Directory.Exists(files.PathFor("workspaces"))); Assert.Equal(plan.Select(x => x.SourceStorageReference), await References(w));
        await files.Write(plan[1].SourceStorageReference, original);
    }
    [Theory]
    [InlineData("../outside.pdf")]
    [InlineData("/outside.pdf")]
    [InlineData("C:\\outside.pdf")]
    public async Task UnsafeSourcePath_IsRejected(string unsafeReference)
    {
        using var files = new Files(); var (w, plan) = await Arrange(files, 1);
        await using var db = Db();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE financial_document_inbox_items SET storage_reference={unsafeReference} WHERE workspace_id={w}");
        await Assert.ThrowsAsync<IOException>(() => new FinancialDocumentWorkspaceCutover(db, files.Root).DryRunAsync(w, 1, default));
        Assert.False(Directory.Exists(files.PathFor("workspaces"))); Assert.True(File.Exists(files.PathFor(plan[0].SourceStorageReference)));
    }
    [Fact]
    public async Task LinkedSourceFile_IsRejectedWithoutFollowingIt()
    {
        using var files = new Files(); var (w, plan) = await Arrange(files, 1);
        var source = files.PathFor(plan[0].SourceStorageReference); var preserved = source + ".preserved";
        File.Move(source, preserved); File.CreateSymbolicLink(source, preserved);
        await using var db = Db();
        await Assert.ThrowsAsync<IOException>(() => new FinancialDocumentWorkspaceCutover(db, files.Root).DryRunAsync(w, 1, default));
        Assert.False(Directory.Exists(files.PathFor("workspaces")));
    }
    [Fact]
    public async Task LinkedTargetDirectory_OrForgedTargetNamespace_CannotEscapeWorkspace()
    {
        using var files = new Files(); var (w, plan) = await Arrange(files, 1);
        await using var db = Db(); var tool = new FinancialDocumentWorkspaceCutover(db, files.Root);
        var forged = new[] { plan[0] with { TargetStorageReference = $"workspaces/{Guid.NewGuid():N}/{plan[0].DocumentId:N}.pdf" } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => tool.ApplyAsync(w, 1, forged, true, default));
        Directory.CreateSymbolicLink(files.PathFor("workspaces"), Path.GetDirectoryName(files.PathFor(plan[0].SourceStorageReference))!);
        await Assert.ThrowsAsync<IOException>(() => tool.ApplyAsync(w, 1, plan, true, default));
        Assert.Equal(plan[0].SourceStorageReference, Assert.Single(await References(w)));
    }
    [Fact]
    public async Task ChangedCompareAndSwapPredicate_RollsBackWithoutOverwritingTheNewReference()
    {
        using var files = new Files(); var (w, plan) = await Arrange(files);
        await using var db = PostgresFixture.CreateDbContext(ConnectionString, new ChangeExpectedReference());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new FinancialDocumentWorkspaceCutover(db, files.Root).ApplyAsync(w, 2, plan, true, default));
        Assert.Equal("StorageReferenceChanged", error.Message);
        Assert.Equal(plan.Select(x => x.SourceStorageReference), await References(w));
    }
    [Fact]
    public async Task ForeignWorkspaceChangedReferenceOrUnapprovedWindow_CannotBeApplied()
    {
        using var files = new Files(); var (w, plan) = await Arrange(files); var (other, _) = await Arrange(files);
        await using var db = Db(); var tool = new FinancialDocumentWorkspaceCutover(db, files.Root);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tool.ApplyAsync(w, 2, plan, false, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tool.ApplyAsync(other, 2, plan, true, default));
        var changed = $"{Guid.NewGuid():N}/{plan[0].DocumentId:N}.pdf";
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE financial_document_inbox_items SET storage_reference={changed} WHERE workspace_id={w} AND id={plan[0].DocumentId}");
        Assert.Equal("StorageReferenceChanged", (await Assert.ThrowsAsync<InvalidOperationException>(() => tool.ApplyAsync(w, 2, plan, true, default))).Message);
        Assert.False(Directory.Exists(files.PathFor("workspaces")));
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FailureAfterCopyOrDuringUpdates_RollsBackEveryReference_AndRetryReusesCopies(int failAt)
    {
        using var files = new Files(); var (w, plan) = await Arrange(files);
        await using (var failing = PostgresFixture.CreateDbContext(ConnectionString, new FailReferenceUpdate(failAt)))
            await Assert.ThrowsAsync<InvalidOperationException>(() => new FinancialDocumentWorkspaceCutover(failing, files.Root).ApplyAsync(w, 2, plan, true, default));
        Assert.Equal(plan.Select(x => x.SourceStorageReference), await References(w));
        Assert.All(plan, x => { Assert.True(File.Exists(files.PathFor(x.SourceStorageReference))); Assert.True(File.Exists(files.PathFor(x.TargetStorageReference))); });
        await using var retry = Db();
        Assert.Equal("Migrated", (await new FinancialDocumentWorkspaceCutover(retry, files.Root).ApplyAsync(w, 2, plan, true, default)).Status);
    }
    private sealed class FailReferenceUpdate(int failAt) : DbCommandInterceptor
    {
        private int updates;
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDATE financial_document_inbox_items SET storage_reference", StringComparison.Ordinal))
            {
                Assert.Contains("workspace_id=", command.CommandText); Assert.Contains("AND storage_reference=", command.CommandText);
                if (++updates == failAt) throw new InvalidOperationException("SyntheticReferenceUpdateFailure");
            }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class ChangeExpectedReference : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDATE financial_document_inbox_items SET storage_reference", StringComparison.Ordinal))
                command.Parameters[3].Value = "synthetic-stale-reference"; // Simulate a lost CAS; affected rows must be zero.
            return ValueTask.FromResult(result);
        }
    }
}
