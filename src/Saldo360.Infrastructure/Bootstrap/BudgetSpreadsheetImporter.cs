using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Saldo360.Application.Bootstrap;
using Saldo360.Application.Workspaces;
using Saldo360.Domain.Bootstrap;
using Saldo360.Domain.Common;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Workspaces;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Bootstrap;

/// <summary>Offline administrative operation. Never registered in the API, jobs or startup.</summary>
public sealed class BudgetSpreadsheetImporter(Saldo360DbContext db, TimeProvider time)
{
    public const string RequiredMigration = "20261022165204_AddBudgetBootstrapExtensions";
    private sealed record Assessment(Guid? Owner, SpreadsheetDomainGraph.Result Graph, Dictionary<string, long> Counts);

    public async Task<SpreadsheetDryRunResult> DryRunAsync(Guid w, SpreadsheetSnapshot source, CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges()) throw new InvalidOperationException("DedicatedCleanContextRequired");
        var plan = new BudgetSpreadsheetMapper().Map(w, source);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
        if (await Scalar("SELECT current_setting('transaction_read_only')", w, ct) is not "on") throw new InvalidOperationException("ReadOnlyTransactionRequired");
        var state = await Assess(plan, ct);
        if (db.ChangeTracker.Entries().Any()) throw new InvalidOperationException("DryRunMustNotTrackOrStageEntities");
        await tx.CommitAsync(ct);
        return new(w, state.Owner, plan.SourceHash, plan.PlanHash, source.ReadAtUtc, time.GetUtcNow(), true, state.Graph.Items, state.Counts);
    }

    /// <summary>Future path. Explicit review+fresh capture required; full preflight under writer/reset root lock before staging.</summary>
    public async Task<int> ExecuteApprovedAsync(Guid w, SpreadsheetSnapshot reviewedSource, SpreadsheetSnapshot freshSource,
        SpreadsheetDryRunResult approval, string approvedReportHash, CancellationToken ct = default)
    {
        if (!approval.CanExecute || approval.ReportHash != approvedReportHash || approval.WorkspaceId != w || approval.OwnerUserId is null)
            throw new InvalidOperationException("ExecutableDryRunApprovalRequired");
        reviewedSource.RequireUnchanged(freshSource);
        var checkedAt = time.GetUtcNow();
        if (freshSource.ReadAtUtc <= approval.DestinationReadAtUtc || freshSource.ReadAtUtc > checkedAt.AddMinutes(1)
            || checkedAt - freshSource.ReadAtUtc > TimeSpan.FromMinutes(5)) throw new InvalidOperationException("FreshSourceCaptureAfterReviewRequired");
        var plan = new BudgetSpreadsheetMapper().Map(w, reviewedSource);
        if (plan.SourceHash != approval.SourceHash || plan.PlanHash != approval.PlanHash) throw new InvalidOperationException("PlanChangedNewDryRunRequired");
        if (db.Database.CurrentTransaction is not null || db.ChangeTracker.Entries().Any()) throw new InvalidOperationException("DedicatedCleanContextRequired");
        using var writer = FinancialWriteScope.EnterOperation(w);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Also protects callers not using the configured interceptors. No child locks before root.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM financial_workspaces WHERE id={w} FOR UPDATE", ct);
            var state = await Assess(plan, ct);
            if (state.Owner != approval.OwnerUserId || !SpreadsheetDryRunResult.PermitsExecution(state.Graph.Items)) throw new InvalidOperationException("PreflightRejectedNoWrites");
            var pending = state.Graph.Items.Where(i => i.IsEntity && i.Status == BootstrapDisposition.Created).ToArray();
            var now = time.GetUtcNow();
            // Every domain object was constructed/validated before the first Add. One context, SaveChanges, transaction.
            foreach (var item in pending)
            {
                var entity = state.Graph.Entities[item.ExternalReference];
                db.Add(entity);
                db.FinancialBootstrapProvenance.Add(FinancialBootstrapProvenance.Create(w, SpreadsheetSnapshot.System, SpreadsheetSnapshot.Document,
                    item.ExternalReference, item.ContentHash, item.EntityType.ToString(), entity.Id,
                    System.Text.Json.JsonSerializer.Serialize(new { sourceHash = plan.SourceHash, item.SourceLocation, item.MetadataJson }), now));
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return pending.Length;
        }
        catch { await tx.RollbackAsync(CancellationToken.None); db.ChangeTracker.Clear(); throw; }
    }

    private async Task<Assessment> Assess(SpreadsheetImportPlan plan, CancellationToken ct)
    {
        var w = plan.WorkspaceId;
        var root = await db.FinancialWorkspaces.AsNoTracking().SingleOrDefaultAsync(x => x.Id == w, ct);
        var ownerValid = root is not null && await db.Users.AsNoTracking().AnyAsync(x => x.Id == root.OwnerUserId, ct);
        var items = plan.Items.ToList();
        void Reject(string code, BootstrapDisposition status = BootstrapDisposition.Invalid) => items.Add(new("PREFLIGHT:" + code, SpreadsheetEntity.Validation, new("Destino"), "PostgreSQL/read-only preflight", Status: status, Reason: code));
        if (root is null) Reject("WorkspaceNotFound");
        else if (root.Type != FinancialWorkspaceType.Personal || !root.IsActive || !ownerValid) Reject("ActivePersonalAndPersistedOwnerRequired");
        var migrations = (await db.Database.GetAppliedMigrationsAsync(ct)).ToArray();
        var schemaReady = migrations.Contains(RequiredMigration, StringComparer.Ordinal);
        if (!schemaReady) Reject("T09.1MigrationNotApplied:DoNotApplyAutomatically");
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var entity in db.Model.GetEntityTypes().Where(e => e.FindProperty("WorkspaceId") is not null).OrderBy(e => e.GetTableName(), StringComparer.Ordinal))
        {
            var table = entity.GetTableName()!;
            var exists = await Scalar("SELECT to_regclass('public." + table.Replace("'", "''", StringComparison.Ordinal) + "') IS NOT NULL", w, ct) == "True";
            counts[table] = exists ? long.Parse((await Scalar("SELECT count(*) FROM public.\"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\" WHERE workspace_id=@w", w, ct))!) : -1;
            if (!exists && schemaReady) Reject("MissingTable:" + table);
        }
        counts["__migrations"] = migrations.Length;
        var ledger = schemaReady ? await db.FinancialBootstrapProvenance.AsNoTracking().Where(x => x.WorkspaceId == w).ToListAsync(ct) : [];
        var existing = new Dictionary<string, Entity>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i]; if (!item.IsEntity) continue;
            var provenance = ledger.Where(p => p.SourceSystem == SpreadsheetSnapshot.System && p.SourceDocument == SpreadsheetSnapshot.Document && p.ExternalReference == item.ExternalReference).ToArray();
            if (provenance.Length == 0) continue;
            if (provenance.Length != 1 || provenance[0].ContentHash != item.ContentHash || provenance[0].EntityType != item.EntityType.ToString())
            { items[i] = item with { Status = BootstrapDisposition.Conflict, Reason = "ProvenanceContentChanged" }; continue; }
            var entity = await Load(item.EntityType, provenance[0].EntityId, w, ct);
            if (entity is null) { items[i] = item with { Status = BootstrapDisposition.Conflict, Reason = "ProvenanceTargetMissingOrCrossWorkspace" }; continue; }
            existing[item.ExternalReference] = entity;
            // Source-invalid items cannot be made executable by an old ledger.
            if (item.Status == BootstrapDisposition.Created) items[i] = item with { Status = BootstrapDisposition.AlreadyImported, Reason = "SameProvenanceAndContent;DestinationValidationRequired" };
        }
        // Bootstrap is not an overwrite/merge tool. Any unjournaled financial content requires a new review.
        foreach (var entity in db.Model.GetEntityTypes().Where(e => e.FindProperty("WorkspaceId") is not null))
        {
            var table = entity.GetTableName()!;
            if (entity.ClrType == typeof(FinancialResetAudit) || entity.ClrType == typeof(FinancialResetFileCleanup) || entity.ClrType == typeof(FinancialBootstrapProvenance)) continue;
            var represented = ledger.Count(p => TypeFor(p.EntityType) == entity.ClrType);
            if (counts.GetValueOrDefault(table) > represented) Reject("UnmanagedDestinationRows:" + table, BootstrapDisposition.Conflict);
        }
        if (ledger.Any(p => p.SourceSystem != SpreadsheetSnapshot.System || p.SourceDocument != SpreadsheetSnapshot.Document
            || !items.Any(i => i.IsEntity && i.ExternalReference == p.ExternalReference))) Reject("UnreviewedDestinationProvenance", BootstrapDisposition.Conflict);
        var graph = SpreadsheetDomainGraph.Build(plan with { Items = items }, root?.OwnerUserId ?? Guid.Empty, time.GetUtcNow(), existing);
        return new(root?.OwnerUserId, graph, counts);
    }

    private async Task<string?> Scalar(string sql, Guid w, CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction(); command.CommandText = sql;
        var p = command.CreateParameter(); p.ParameterName = "w"; p.Value = w; command.Parameters.Add(p);
        return (await command.ExecuteScalarAsync(ct))?.ToString();
    }
    private static Type? TypeFor(string kind) => Enum.TryParse<SpreadsheetEntity>(kind, out var k) ? k switch
    {
        SpreadsheetEntity.Category => typeof(Saldo360.Domain.Categories.FinancialCategory), SpreadsheetEntity.Account => typeof(Saldo360.Domain.Accounts.FinancialAccount),
        SpreadsheetEntity.Card => typeof(FinancialCreditCard), SpreadsheetEntity.AccountRecurrence or SpreadsheetEntity.CardRecurrence => typeof(Saldo360.Domain.Recurrences.FinancialRecurrence),
        SpreadsheetEntity.PlannedTransaction => typeof(Saldo360.Domain.Planning.FinancialPlannedTransaction), SpreadsheetEntity.Purchase => typeof(FinancialCreditCardPurchase),
        SpreadsheetEntity.Invoice => typeof(FinancialCreditCardInvoice), SpreadsheetEntity.Installment => typeof(FinancialCreditCardInstallment),
        SpreadsheetEntity.KnownInstallment => typeof(FinancialCreditCardImportedInstallment), SpreadsheetEntity.MonthlyAggregateTarget => typeof(Saldo360.Domain.Budgets.FinancialMonthlyAggregateTarget), _ => null
    } : null;
    private async Task<Entity?> Load(SpreadsheetEntity kind, Guid id, Guid w, CancellationToken ct) => kind switch
    {
        SpreadsheetEntity.Category => await db.FinancialCategories.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == w, ct),
        SpreadsheetEntity.Account => await db.FinancialAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == w, ct),
        SpreadsheetEntity.Card => await db.FinancialCreditCards.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == w, ct),
        SpreadsheetEntity.AccountRecurrence or SpreadsheetEntity.CardRecurrence => await db.FinancialRecurrences.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == w, ct),
        SpreadsheetEntity.PlannedTransaction => await db.FinancialPlannedTransactions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == w, ct),
        SpreadsheetEntity.Purchase => await db.FinancialCreditCardPurchases.AsNoTracking().Include(x => x.Installments).SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == w, ct),
        SpreadsheetEntity.Installment => await db.FinancialCreditCardInstallments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == w, ct),
        SpreadsheetEntity.Invoice => await db.FinancialCreditCardInvoices.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == w, ct),
        SpreadsheetEntity.KnownInstallment => await db.FinancialCreditCardImportedInstallments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == w, ct),
        SpreadsheetEntity.MonthlyAggregateTarget => await db.FinancialMonthlyAggregateTargets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.WorkspaceId == w, ct),
        _ => null
    };
}
