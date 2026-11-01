using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Saldo360.Application.Bootstrap;
using Saldo360.Application.Workspaces;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Bootstrap;
using Saldo360.Domain.Common;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Workspaces;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Bootstrap;

/// <summary>Offline administrative operation. Never registered in the API, jobs or startup.</summary>
public enum BudgetSpreadsheetImportScope { FullLegacyBootstrap, FixedExpensesOnly }

/// <param name="accountMap">Associação EXPLÍCITA (FixedExpensesOnly): referência da planilha (ACCOUNT:ItauPF / ACCOUNT:ItauEmpresas) para a
/// conta existente do workspace. A conta nunca é modificada; é só dependência de leitura.</param>
public sealed class BudgetSpreadsheetImporter(Saldo360DbContext db, TimeProvider time, BudgetSpreadsheetImportScope scope = BudgetSpreadsheetImportScope.FullLegacyBootstrap,
    IReadOnlyDictionary<string, Guid>? accountMap = null)
{
    public static readonly string[] MappableAccounts = ["ACCOUNT:ItauPF", "ACCOUNT:ItauEmpresas"];
    private const string ExplicitMapping = "ExplicitMapping", AutomaticLink = "AutomaticPluggyLink", FromLedger = "Ledger";
    private IReadOnlyDictionary<string, Guid> AccountMap => accountMap ?? new Dictionary<string, Guid>();

    public const string RequiredMigration = "20261022165204_AddBudgetBootstrapExtensions";
    private sealed record Assessment(Guid? Owner, SpreadsheetDomainGraph.Result Graph, Dictionary<string, long> Counts);

    public async Task<SpreadsheetDryRunResult> DryRunAsync(Guid w, SpreadsheetSnapshot source, CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges()) throw new InvalidOperationException("DedicatedCleanContextRequired");
        var plan = Map(w, source);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
        if (await Scalar("SELECT current_setting('transaction_read_only')", w, ct) is not "on") throw new InvalidOperationException("ReadOnlyTransactionRequired");
        var state = await Assess(plan, ct);
        if (db.ChangeTracker.Entries().Any()) throw new InvalidOperationException("DryRunMustNotTrackOrStageEntities");
        await tx.CommitAsync(ct);
        var destinationReadAt = time.GetUtcNow();
        var destinationHash = SpreadsheetSnapshot.Hash(new { WorkspaceId = w, state.Owner, ReadAtUtc = destinationReadAt,
            Counts = state.Counts.OrderBy(x => x.Key, StringComparer.Ordinal) });
        return new(w, state.Owner, plan.SourceHash, plan.PlanHash, source.ReadAtUtc, destinationReadAt, true, state.Graph.Items, state.Counts,
            DestinationSnapshotHash: destinationHash);
    }

    /// <summary>Future path. Explicit review+fresh capture required; full preflight under writer/reset root lock before staging.</summary>
    public async Task<int> ExecuteApprovedAsync(Guid w, SpreadsheetSnapshot reviewedSource, SpreadsheetSnapshot freshSource,
        SpreadsheetDryRunResult approval, string approvedReportHash, CancellationToken ct = default)
    {
        if (!approval.CanExecute || approval.ReportHash != approvedReportHash || approval.WorkspaceId != w || approval.OwnerUserId is null)
            throw new InvalidOperationException("ExecutableDryRunApprovalRequired");
        if (scope == BudgetSpreadsheetImportScope.FullLegacyBootstrap) reviewedSource.RequireUnchanged(freshSource);
        else if (Map(w, reviewedSource).SourceHash != Map(w, freshSource).SourceHash) throw new InvalidOperationException("SourceChangedAfterReview");
        var checkedAt = time.GetUtcNow();
        if (freshSource.ReadAtUtc <= approval.DestinationReadAtUtc || freshSource.ReadAtUtc > checkedAt.AddMinutes(1)
            || checkedAt - freshSource.ReadAtUtc > TimeSpan.FromMinutes(5)) throw new InvalidOperationException("FreshSourceCaptureAfterReviewRequired");
        var plan = Map(w, reviewedSource);
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
            // The association the user reviewed is the one that is persisted.
            foreach (var reviewed in approval.Items.Where(i => i.EntityType == SpreadsheetEntity.Account && i.AccountResolution?.SelectedAccountId is not null))
                if (state.Graph.Items.FirstOrDefault(i => i.ExternalReference == reviewed.ExternalReference)?.AccountResolution?.SelectedAccountId != reviewed.AccountResolution!.SelectedAccountId)
                    throw new InvalidOperationException("AccountAssociationChangedNewDryRunRequired");
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
            // Associated (not created) accounts: preserve the association in the ledger; the account row itself is never touched.
            var associated = state.Graph.Items.Where(i => i.IsEntity && i.EntityType == SpreadsheetEntity.Account && i.Status == BootstrapDisposition.AlreadyImported
                && i.AccountResolution is { Method: ExplicitMapping or AutomaticLink }).ToArray();
            foreach (var item in associated)
                db.FinancialBootstrapProvenance.Add(FinancialBootstrapProvenance.Create(w, SpreadsheetSnapshot.System, SpreadsheetSnapshot.Document,
                    item.ExternalReference, item.ContentHash, item.EntityType.ToString(), item.AccountResolution!.SelectedAccountId!.Value,
                    System.Text.Json.JsonSerializer.Serialize(new { sourceHash = plan.SourceHash, item.SourceLocation, associationMethod = item.AccountResolution.Method }), now));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return pending.Length + associated.Length;
        }
        catch { await tx.RollbackAsync(CancellationToken.None); db.ChangeTracker.Clear(); throw; }
    }

    private SpreadsheetImportPlan Map(Guid workspaceId, SpreadsheetSnapshot source) => scope == BudgetSpreadsheetImportScope.FixedExpensesOnly
        ? new BudgetSpreadsheetMapper().MapFixedExpenses(workspaceId, source)
        : new BudgetSpreadsheetMapper().Map(workspaceId, source);

    private async Task<Assessment> Assess(SpreadsheetImportPlan plan, CancellationToken ct)
    {
        var w = plan.WorkspaceId;
        if (AccountMap.Count > 0 && (scope != BudgetSpreadsheetImportScope.FixedExpensesOnly || AccountMap.Any(p => !MappableAccounts.Contains(p.Key, StringComparer.Ordinal) || p.Value == Guid.Empty)))
            throw new InvalidOperationException("InvalidAccountMap");
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
        if (scope == BudgetSpreadsheetImportScope.FixedExpensesOnly)
        {
            void Adopt(int index, Entity entity, string reason)
            {
                existing[items[index].ExternalReference] = entity;
                items[index] = items[index] with { Status = BootstrapDisposition.AlreadyImported, Reason = reason };
            }
            var categories = await db.FinancialCategories.AsNoTracking().Where(x => x.WorkspaceId == w && x.ParentCategoryId == null).ToListAsync(ct);
            var accounts = await db.FinancialAccounts.AsNoTracking().Where(x => x.WorkspaceId == w).ToListAsync(ct);
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (!item.IsEntity || item.Status != BootstrapDisposition.Created || existing.ContainsKey(item.ExternalReference)) continue;
                if (item.EntityType == SpreadsheetEntity.Category)
                {
                    var matches = categories.Where(x => x.Type == Saldo360.Domain.Categories.CategoryType.Expense
                        && Saldo360.Domain.Categories.FinancialCategory.MatchKey(x.Name) == Saldo360.Domain.Categories.FinancialCategory.MatchKey(item.Payload.Name)).ToList();
                    if (matches.Count == 1) Adopt(i, matches[0], "ExistingCategoryMatchedExactly");
                }
            }
            await ResolveAccounts(w, items, existing, accounts, ct);
            var recurrences = await db.FinancialRecurrences.AsNoTracking().Where(x => x.WorkspaceId == w).ToListAsync(ct);
            var planned = await db.FinancialPlannedTransactions.AsNoTracking().Where(x => x.WorkspaceId == w && !x.IsCancelled).ToListAsync(ct);
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (!item.IsEntity || item.Status != BootstrapDisposition.Created || existing.ContainsKey(item.ExternalReference)
                    || item.Payload.Account is null || item.Payload.Category is null
                    || !existing.TryGetValue(item.Payload.Account, out var accountEntity) || !existing.TryGetValue(item.Payload.Category, out var categoryEntity)) continue;
                if (item.EntityType == SpreadsheetEntity.AccountRecurrence)
                {
                    var matches = recurrences.Where(x => x.FinancialAccountId == accountEntity.Id && x.FinancialCategoryId == categoryEntity.Id
                        && x.Description == item.Payload.Name && x.Amount == item.Payload.Amount && x.StartDate == item.Payload.Date
                        && x.EndDate == item.Payload.EndDate && x.DayOfMonth == item.Payload.Day).ToList();
                    if (matches.Count == 1) Adopt(i, matches[0], "ExistingRecurrenceMatchedExactly");
                }
                else if (item.EntityType == SpreadsheetEntity.PlannedTransaction)
                {
                    var matches = planned.Where(x => x.AccountId == accountEntity.Id && x.CategoryId == categoryEntity.Id
                        && x.Description == item.Payload.Name && x.Amount == item.Payload.Amount && x.DueDate == item.Payload.Date).ToList();
                    if (matches.Count == 1) Adopt(i, matches[0], "ExistingPlannedTransactionMatchedExactly");
                }
            }
        }
        // Bootstrap is not an overwrite/merge tool. Any unjournaled financial content requires a new review.
        foreach (var entity in db.Model.GetEntityTypes().Where(e => e.FindProperty("WorkspaceId") is not null && scope == BudgetSpreadsheetImportScope.FullLegacyBootstrap))
        {
            var table = entity.GetTableName()!;
            if (entity.ClrType == typeof(FinancialResetAudit) || entity.ClrType == typeof(FinancialResetFileCleanup) || entity.ClrType == typeof(FinancialBootstrapProvenance)) continue;
            var represented = ledger.Count(p => TypeFor(p.EntityType) == entity.ClrType);
            if (counts.GetValueOrDefault(table) > represented) Reject("UnmanagedDestinationRows:" + table, BootstrapDisposition.Conflict);
        }
        if (scope == BudgetSpreadsheetImportScope.FullLegacyBootstrap && ledger.Any(p => p.SourceSystem != SpreadsheetSnapshot.System || p.SourceDocument != SpreadsheetSnapshot.Document
            || !items.Any(i => i.IsEntity && i.ExternalReference == p.ExternalReference))) Reject("UnreviewedDestinationProvenance", BootstrapDisposition.Conflict);
        var graph = SpreadsheetDomainGraph.Build(plan with { Items = items }, root?.OwnerUserId ?? Guid.Empty, time.GetUtcNow(), existing,
            accountsAreReadOnlyDependencies: scope == BudgetSpreadsheetImportScope.FixedExpensesOnly);
        return new(root?.OwnerUserId, graph, counts);
    }


    private static string Fold(string? text) => new string((text ?? "").Normalize(System.Text.NormalizationForm.FormD)
        .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant();

    /// <summary>
    /// Associates the spreadsheet's PF/PJ accounts with existing workspace accounts. The name alone never concludes a match:
    /// automatic association needs exactly one active bank account of the scope, institution-compatible AND proven by a Pluggy bank link.
    /// Otherwise it is blocked with the candidates listed, until the user passes an explicit, validated association (preserved in the ledger).
    /// Associated accounts are read-only dependencies; they are never created, updated or compared on opening balance.
    /// </summary>
    private async Task ResolveAccounts(Guid w, List<SpreadsheetPlanItem> items, Dictionary<string, Entity> existing, List<FinancialAccount> accounts, CancellationToken ct)
    {
        var links = await db.PluggyAccountLinks.AsNoTracking().Where(x => x.WorkspaceId == w && x.Kind == Saldo360.Domain.Pluggy.PluggyLinkedAccountKind.Bank && x.FinancialAccountId != null).ToListAsync(ct);
        var cardIds = (await db.FinancialCreditCards.AsNoTracking().Where(x => x.WorkspaceId == w).Select(x => x.Id).ToListAsync(ct)).ToHashSet();
        AccountCandidate Describe(FinancialAccount a, SpreadsheetPayload p, params string[] issues)
        {
            var link = links.FirstOrDefault(l => l.FinancialAccountId == a.Id);
            var divergent = new List<string>();
            if (!a.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase)) divergent.Add("Name");
            if (!string.Equals(a.Institution, p.Institution, StringComparison.OrdinalIgnoreCase)) divergent.Add("Institution");
            if (a.Type != AccountType.Checking) divergent.Add("Type");
            if (a.OpeningBalance is not null) divergent.Add("OpeningBalance");
            if (a.OpeningBalanceDate is not null) divergent.Add("OpeningBalanceDate");
            if (a.ResponsibleUserId is not null) divergent.Add("ResponsibleUserId");
            if (a.Currency != FinancialAccount.DefaultCurrency) divergent.Add("Currency");
            return new(a.Id, a.Name, a.Scope.ToString(), a.Institution, a.Type.ToString(), a.IsActive, link is not null, link?.InstitutionName,
                link?.Status.ToString(), a.OpeningBalance, a.OpeningBalanceDate, divergent.ToArray(), issues);
        }
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (!item.IsEntity || item.EntityType != SpreadsheetEntity.Account || !Enum.TryParse<FinancialScope>(item.Payload.Scope, out var wanted)) continue;
            var reference = item.ExternalReference; var key = Fold(item.Payload.Institution);
            var hasMap = AccountMap.TryGetValue(reference, out var mappedId);
            if (existing.TryGetValue(reference, out var preserved))
            {
                // Association preserved by a previous execution (ledger).
                if (preserved is not FinancialAccount kept || item.Status != BootstrapDisposition.AlreadyImported) continue;
                if (hasMap && mappedId != kept.Id)
                {
                    existing.Remove(reference);
                    items[i] = item with { Status = BootstrapDisposition.Conflict, Reason = "AccountMappingDiffersFromPreservedAssociation",
                        AccountResolution = new("MappingDiffersFromLedger", kept.Id, [Describe(kept, item.Payload)]) };
                    continue;
                }
                items[i] = item with { AccountResolution = new(FromLedger, kept.Id, [Describe(kept, item.Payload)]) };
                continue;
            }
            if (item.Status != BootstrapDisposition.Created) continue;
            var inScope = accounts.Where(a => a.Scope == wanted && a.IsActive && FinancialAccount.IsBankType(a.Type)).ToList();
            var related = inScope.Where(a => Fold(a.Institution).Contains(key, StringComparison.Ordinal) || Fold(a.Name).Contains(key, StringComparison.Ordinal)
                || links.Any(l => l.FinancialAccountId == a.Id && Fold(l.InstitutionName).Contains(key, StringComparison.Ordinal))).ToList();
            void Block(string reason, string method) => items[i] = item with { Status = BootstrapDisposition.Conflict, Reason = reason,
                AccountResolution = new(method, null, related.Select(a => Describe(a, item.Payload)).ToArray(),
                    $"Escolha um candidato e informe --account-map {reference}=<id>.") };
            if (hasMap)
            {
                var target = accounts.FirstOrDefault(a => a.Id == mappedId);
                var issue = target is null ? cardIds.Contains(mappedId) ? "AccountMappingTargetIsCard" : "AccountMappingTargetNotFoundInWorkspace"
                    : !target.IsActive ? "AccountMappingTargetInactive" : !FinancialAccount.IsBankType(target.Type) ? "AccountMappingTargetNotBankAccount"
                    : target.Scope != wanted ? "AccountMappingScopeMismatch" : null;
                if (issue is not null) { Block(issue, "InvalidExplicitMapping"); continue; }
                existing[reference] = target!;
                items[i] = item with { Status = BootstrapDisposition.AlreadyImported, Reason = "ExplicitAccountMapping", AccountResolution = new(ExplicitMapping, target!.Id, [Describe(target, item.Payload)]) };
                continue;
            }
            if (related.Count == 0)
            {
                // Nothing compatible exists, so creating cannot duplicate an account. Other accounts of the scope are listed for transparency.
                items[i] = item with { AccountResolution = new("NoCompatibleAccount", null, inScope.Select(a => Describe(a, item.Payload, "InstitutionNotCompatible")).ToArray()) };
                continue;
            }
            var only = related.Count == 1 ? related[0] : null;
            var proven = only is not null && links.Any(l => l.FinancialAccountId == only.Id && (Fold(only.Institution).Contains(key, StringComparison.Ordinal)
                || Fold(l.InstitutionName).Contains(key, StringComparison.Ordinal)));
            if (proven)
            {
                existing[reference] = only!;
                items[i] = item with { Status = BootstrapDisposition.AlreadyImported, Reason = "AutomaticAccountMatchPluggyLink", AccountResolution = new(AutomaticLink, only!.Id, [Describe(only, item.Payload)]) };
            }
            else if (only is null) Block("AccountAssociationRequired:AmbiguousCandidates", "AmbiguousCandidates");
            else Block("AccountAssociationRequired:InsufficientProof", "InsufficientProof");
        }
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
