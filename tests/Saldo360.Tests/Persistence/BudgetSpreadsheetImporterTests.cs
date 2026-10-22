using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Saldo360.Application.Bootstrap;
using Saldo360.Domain.Users;
using Saldo360.Domain.Workspaces;
using Saldo360.Infrastructure.Bootstrap;
using Saldo360.Infrastructure.Persistence;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Persistence;

[Collection(PostgresCollection.Name)]
public sealed class BudgetSpreadsheetImporterTests(PostgresFixture postgres)
{
    private sealed class NoDml : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public List<string> Commands { get; } = [];
        private void Check(DbCommand command)
        {
            Commands.Add(command.CommandText);
            if (Armed && System.Text.RegularExpressions.Regex.IsMatch(command.CommandText, @"\b(INSERT|UPDATE|DELETE|CREATE|ALTER|DROP|TRUNCATE)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) throw new InvalidOperationException("UnexpectedWrite");
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData e, InterceptionResult<DbDataReader> r, CancellationToken ct = default) { Check(command); return ValueTask.FromResult(r); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData e, InterceptionResult<int> r, CancellationToken ct = default) { Check(command); return ValueTask.FromResult(r); }
        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData e, InterceptionResult<object> r, CancellationToken ct = default) { Check(command); return ValueTask.FromResult(r); }
    }
    private async Task<(string Connection, Guid Workspace, Guid Owner)> Setup()
    {
        var connection = await postgres.CreateDatabaseAsync();
        await using var db = PostgresFixture.CreateDbContext(connection);
        var owner = ApplicationUser.Create("https://synthetic.invalid", Guid.NewGuid().ToString(), "Synthetic", BudgetSpreadsheetFixture.ReadAt);
        var workspace = FinancialWorkspace.CreatePersonal(owner.Id, BudgetSpreadsheetFixture.ReadAt);
        db.Users.Add(owner); db.FinancialWorkspaces.Add(workspace); await db.SaveChangesAsync();
        return (connection, workspace.Id, owner.Id);
    }

    [Fact] public async Task RealPostgresDryRun_IsReadOnly_StagesNothing_AndLeavesAllFinancialTablesEmpty()
    {
        var (c, w, owner) = await Setup(); var guard = new NoDml { Armed = true };
        await using (var db = PostgresFixture.CreateDbContext(c, guard))
        {
            var report = await new BudgetSpreadsheetImporter(db, TimeProvider.System).DryRunAsync(w, BudgetSpreadsheetFixture.Source());
            Assert.Equal(owner, report.OwnerUserId); Assert.True(report.DatabaseReadOnly); Assert.False(report.CanExecute);
            Assert.All(report.DestinationCounts.Where(x => x.Key != "__migrations"), p => Assert.Equal(0, p.Value));
            Assert.Empty(db.ChangeTracker.Entries());
            Assert.Contains(guard.Commands, s => s == "SET TRANSACTION READ ONLY");
        }
        await using var verify = PostgresFixture.CreateDbContext(c);
        Assert.Empty(verify.FinancialAccounts); Assert.Empty(verify.FinancialBootstrapProvenance); Assert.Empty(verify.FinancialCreditCardPurchases);
        Assert.Single(verify.FinancialWorkspaces); Assert.Empty(verify.Families);
    }

    [Fact] public async Task SyntheticFutureExecution_IsAtomicAndIdempotent_AllEntitiesHaveProvenance_NoBankTransactions()
    {
        var (c, w, _) = await Setup(); var source = BudgetSpreadsheetFixture.ExecutableSyntheticSource();
        SpreadsheetDryRunResult report;
        await using (var dry = PostgresFixture.CreateDbContext(c)) report = await new BudgetSpreadsheetImporter(dry, TimeProvider.System).DryRunAsync(w, source);
        var reviewHash = report.ReportHash;
        report = System.Text.Json.JsonSerializer.Deserialize<SpreadsheetDryRunResult>(System.Text.Json.JsonSerializer.Serialize(report, SpreadsheetSnapshot.Json), SpreadsheetSnapshot.Json)!;
        Assert.Equal(reviewHash, report.ReportHash);
        Assert.True(report.CanExecute, string.Join(";", report.Items.Where(i => i.Status is BootstrapDisposition.Invalid or BootstrapDisposition.Conflict).Select(i => i.ExternalReference + ":" + i.Reason)));
        var fresh = source with { ReadAtUtc = DateTimeOffset.UtcNow };
        await using (var write = PostgresFixture.CreateDbContext(c))
            Assert.Equal(report.ProvenanceRecords, await new BudgetSpreadsheetImporter(write, TimeProvider.System).ExecuteApprovedAsync(w, source, fresh, report, report.ReportHash));
        await using (var dry = PostgresFixture.CreateDbContext(c))
        {
            var again = await new BudgetSpreadsheetImporter(dry, TimeProvider.System).DryRunAsync(w, source);
            Assert.True(again.CanExecute, string.Join(";", again.Items.Where(i => i.Status is BootstrapDisposition.Invalid or BootstrapDisposition.Conflict).Select(i => i.ExternalReference + ":" + i.Reason)));
            Assert.Equal(report.ProvenanceRecords, again.CountsByStatus["AlreadyImported"]); Assert.Equal(0, again.CountsByStatus["Created"]);
        }
        await using (var write = PostgresFixture.CreateDbContext(c)) Assert.Equal(0, await new BudgetSpreadsheetImporter(write, TimeProvider.System).ExecuteApprovedAsync(w, source, fresh, report, report.ReportHash));
        await using var verify = PostgresFixture.CreateDbContext(c);
        Assert.Equal(report.ProvenanceRecords, await verify.FinancialBootstrapProvenance.CountAsync());
        Assert.Equal(10, await verify.FinancialCategories.CountAsync()); Assert.Equal(2, await verify.FinancialAccounts.CountAsync());
        Assert.Equal(48, await verify.FinancialMonthlyAggregateTargets.CountAsync()); Assert.Equal(7, await verify.FinancialPlannedTransactions.CountAsync());
        Assert.Empty(verify.FinancialTransactions); Assert.Empty(verify.FinancialCreditCardInvoicePayments); Assert.Empty(verify.FinancialRecurrenceMaterializations);
        Assert.Empty(verify.FinancialBudgetTargets); Assert.Empty(verify.Families);
        Assert.All(await verify.FinancialCreditCardInvoices.ToListAsync(), i => Assert.NotEqual("2026-10", i.Competence));
    }

    [Fact] public async Task ProvenanceChangedContentAndEditedDestination_AreConflict_NotAlreadyImported()
    {
        var (c, w, _) = await Setup(); var s = BudgetSpreadsheetFixture.ExecutableSyntheticSource(); SpreadsheetDryRunResult approval;
        await using (var db = PostgresFixture.CreateDbContext(c)) approval = await new BudgetSpreadsheetImporter(db, TimeProvider.System).DryRunAsync(w, s);
        await using (var db = PostgresFixture.CreateDbContext(c)) await new BudgetSpreadsheetImporter(db, TimeProvider.System).ExecuteApprovedAsync(w, s, s with { ReadAtUtc = DateTimeOffset.UtcNow }, approval, approval.ReportHash);
        var changed = BudgetSpreadsheetFixture.ExecutableSyntheticSource(); changed.Sheet("Metas Mensais").Rows[1][4] = BudgetSpreadsheetFixture.Cell(5m);
        await using (var db = PostgresFixture.CreateDbContext(c))
        {
            var report = await new BudgetSpreadsheetImporter(db, TimeProvider.System).DryRunAsync(w, changed);
            Assert.False(report.CanExecute); Assert.Contains(report.Items, i => i.Reason == "ProvenanceContentChanged");
        }
        await using (var edit = PostgresFixture.CreateDbContext(c))
        {
            var category = await edit.FinancialCategories.FirstAsync(); category.Update("Synthetic user edit", true); await edit.SaveChangesAsync();
        }
        await using (var db = PostgresFixture.CreateDbContext(c))
        {
            var report = await new BudgetSpreadsheetImporter(db, TimeProvider.System).DryRunAsync(w, s);
            Assert.False(report.CanExecute); Assert.Contains(report.Items, i => i.Status == BootstrapDisposition.Conflict && i.Reason == "DestinationEntityChangedOrMissing");
        }
    }

    [Fact] public async Task InvalidPreflightAndSourceChanges_RejectBeforeFirstWrite()
    {
        var (c, w, _) = await Setup(); var s = BudgetSpreadsheetFixture.Source(); SpreadsheetDryRunResult report;
        await using (var db = PostgresFixture.CreateDbContext(c)) report = await new BudgetSpreadsheetImporter(db, TimeProvider.System).DryRunAsync(w, s);
        await using (var db = PostgresFixture.CreateDbContext(c))
            await Assert.ThrowsAsync<InvalidOperationException>(() => new BudgetSpreadsheetImporter(db, TimeProvider.System).ExecuteApprovedAsync(w, s, s with { ReadAtUtc = DateTimeOffset.UtcNow }, report, report.ReportHash));
        var valid = BudgetSpreadsheetFixture.ExecutableSyntheticSource();
        await using (var db = PostgresFixture.CreateDbContext(c)) report = await new BudgetSpreadsheetImporter(db, TimeProvider.System).DryRunAsync(w, valid);
        var changed = BudgetSpreadsheetFixture.ExecutableSyntheticSource() with { ReadAtUtc = DateTimeOffset.UtcNow }; changed.Sheet("Parâmetros").Rows[1][1] = BudgetSpreadsheetFixture.Cell(101);
        await using (var db = PostgresFixture.CreateDbContext(c))
            await Assert.ThrowsAsync<InvalidOperationException>(() => new BudgetSpreadsheetImporter(db, TimeProvider.System).ExecuteApprovedAsync(w, valid, changed, report, report.ReportHash));
        await using var verify = PostgresFixture.CreateDbContext(c); Assert.Empty(verify.FinancialCategories); Assert.Empty(verify.FinancialBootstrapProvenance);
    }

    [Fact] public async Task FailureDuringSave_RollsBackWholeGraphAndProvenance()
    {
        var (c, w, _) = await Setup(); var s = BudgetSpreadsheetFixture.ExecutableSyntheticSource(); SpreadsheetDryRunResult report;
        await using (var db = PostgresFixture.CreateDbContext(c)) report = await new BudgetSpreadsheetImporter(db, TimeProvider.System).DryRunAsync(w, s);
        await using (var db = PostgresFixture.CreateDbContext(c))
        {
            // Test-only trigger in this disposable database: fail after graph validation, during actual SQL writes.
            await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION reject_synthetic_target() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'synthetic failure'; END $$; CREATE TRIGGER reject_synthetic_target BEFORE INSERT ON financial_monthly_aggregate_targets FOR EACH ROW EXECUTE FUNCTION reject_synthetic_target()");
            await Assert.ThrowsAsync<DbUpdateException>(() => new BudgetSpreadsheetImporter(db, TimeProvider.System).ExecuteApprovedAsync(w, s, s with { ReadAtUtc = DateTimeOffset.UtcNow }, report, report.ReportHash));
        }
        await using var verify = PostgresFixture.CreateDbContext(c);
        Assert.Empty(verify.FinancialAccounts); Assert.Empty(verify.FinancialCategories); Assert.Empty(verify.FinancialCreditCards); Assert.Empty(verify.FinancialBootstrapProvenance); Assert.Empty(verify.FinancialMonthlyAggregateTargets);
    }

    [Fact] public async Task UnmanagedCategoryAndWrongWorkspace_AreBlockingAndNotAdoptedByName()
    {
        var (c, w, owner) = await Setup();
        await using (var db = PostgresFixture.CreateDbContext(c))
        { db.FinancialCategories.Add(Saldo360.Domain.Categories.FinancialCategory.Create(w, "alimentação", Saldo360.Domain.Categories.CategoryType.Expense, owner, BudgetSpreadsheetFixture.ReadAt)); await db.SaveChangesAsync(); }
        await using (var db = PostgresFixture.CreateDbContext(c))
        {
            var report = await new BudgetSpreadsheetImporter(db, TimeProvider.System).DryRunAsync(w, BudgetSpreadsheetFixture.ExecutableSyntheticSource());
            Assert.False(report.CanExecute); Assert.Contains(report.Items, i => i.Status == BootstrapDisposition.Conflict && i.Reason == "UnmanagedDestinationRows:financial_categories");
        }
        await using (var db = PostgresFixture.CreateDbContext(c))
        {
            var report = await new BudgetSpreadsheetImporter(db, TimeProvider.System).DryRunAsync(Guid.NewGuid(), BudgetSpreadsheetFixture.Source());
            Assert.False(report.CanExecute); Assert.Null(report.OwnerUserId); Assert.Contains(report.Items, i => i.Reason == "WorkspaceNotFound");
        }
    }

    [Fact] public async Task MinimalReadOnlyExport_ReplaysLocally_AndNeverAssumesMissingSchemaIsEmpty()
    {
        var (c, w, owner) = await Setup(); var source = BudgetSpreadsheetFixture.ExecutableSyntheticSource();
        await using var db = PostgresFixture.CreateDbContext(c);
        var direct = await new BudgetSpreadsheetImporter(db, TimeProvider.System).DryRunAsync(w, source);
        var destination = new ReadOnlyBudgetDestinationSnapshot(1, w, owner, "Personal", true, true, DateTimeOffset.UtcNow, true,
            (await db.Database.GetAppliedMigrationsAsync()).ToArray(), direct.DestinationCounts.Where(p => p.Key != "__migrations").ToDictionary(p => p.Key, p => p.Value));
        var offline = destination.DryRun(w, source); Assert.True(offline.CanExecute); Assert.Equal("ReadOnlyDestinationSnapshot", offline.DestinationMode);
        Assert.Equal(direct.PlanHash, offline.PlanHash); Assert.Equal(direct.CountsByAggregate, offline.CountsByAggregate);
        var old = destination with { AppliedMigrations = destination.AppliedMigrations.Where(m => m != BudgetSpreadsheetImporter.RequiredMigration).ToArray(),
            Counts = destination.Counts.Where(p => p.Key is not ("financial_bootstrap_provenance" or "financial_monthly_aggregate_targets")).ToDictionary(p => p.Key, p => p.Value) };
        var blocked = old.DryRun(w, source); Assert.False(blocked.CanExecute); Assert.Equal(-1, blocked.DestinationCounts["financial_bootstrap_provenance"]);
        Assert.Contains(blocked.Items, i => i.Reason.StartsWith("T09.1MigrationNotApplied"));
        Assert.False((destination with { DatabaseReadOnly = false }).DryRun(w, source).CanExecute);
        Assert.False((destination with { OwnerExists = false }).DryRun(w, source).CanExecute);
        Assert.False((destination with { IsActive = false }).DryRun(w, source).CanExecute);
        Assert.False((destination with { WorkspaceType = "Shared" }).DryRun(w, source).CanExecute);
        var counts = new Dictionary<string, long>(destination.Counts) { ["financial_categories"] = 1 };
        Assert.False((destination with { Counts = counts }).DryRun(w, source).CanExecute);
    }
}
