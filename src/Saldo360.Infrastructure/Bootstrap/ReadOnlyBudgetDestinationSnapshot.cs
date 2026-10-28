using System.Text.Json;
using Saldo360.Application.Bootstrap;

namespace Saldo360.Infrastructure.Bootstrap;

/// <summary>Minimal read-only export. Keeps the private workbook local; never pretends to inspect existing entity contents.</summary>
public sealed record ReadOnlyBudgetDestinationSnapshot(int SchemaVersion, Guid WorkspaceId, Guid? OwnerUserId,
    string? WorkspaceType, bool? IsActive, bool OwnerExists, DateTimeOffset ReadAtUtc, bool DatabaseReadOnly,
    string[] AppliedMigrations, Dictionary<string, long> Counts)
{
    // Baseline workspace inventory (T08.3/T08.1); T09.1 adds the two explicit tables below.
    private const string Baseline = "financial_accounts financial_budget_targets financial_budgets financial_categories financial_credit_card_import_batches financial_credit_card_import_items financial_credit_card_imported_installments financial_credit_card_installments financial_credit_card_invoice_adjustments financial_credit_card_invoice_payments financial_credit_card_invoices financial_credit_card_purchases financial_credit_cards financial_debt_installment_reconciliations financial_debt_installments financial_debt_payments financial_debts financial_document_inbox_items financial_email_collector_checkpoints financial_goal_allocations financial_goals financial_planned_invoice_reconciliations financial_planned_transactions financial_planning_monthly_parameters financial_planning_scenarios financial_recurrence_materializations financial_recurrences financial_transactions financial_transfers financial_workspace_reset_audit financial_workspace_reset_file_cleanup service_account_bindings";
    private sealed record TableCount(string Table, long Count);
    public static ReadOnlyBudgetDestinationSnapshot Parse(string json)
    {
        var lines = json.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length > 1 && lines[0].TrimEnd().EndsWith('}'))
        {
            // The shipped psql reader emits bounded NDJSON, not a private database dump.
            var counts = lines.Skip(1).Select(l => JsonSerializer.Deserialize<TableCount>(l, SpreadsheetSnapshot.Json)
                ?? throw new InvalidOperationException("InvalidTableCount")).ToDictionary(x => x.Table, x => x.Count, StringComparer.Ordinal);
            var header = lines[0].TrimEnd();
            json = header[..^1] + ",\"counts\":" + JsonSerializer.Serialize(counts) + "}";
        }
        var result = JsonSerializer.Deserialize<ReadOnlyBudgetDestinationSnapshot>(json, SpreadsheetSnapshot.Json) ?? throw new InvalidOperationException("InvalidDestinationSnapshot");
        if (result.Counts is null || result.AppliedMigrations is null) throw new InvalidOperationException("IncompleteDestinationSnapshot");
        return result;
    }
    public string ContentHash => SpreadsheetSnapshot.Hash(new { SchemaVersion, WorkspaceId, OwnerUserId, WorkspaceType, IsActive, OwnerExists,
        ReadAtUtc, DatabaseReadOnly, AppliedMigrations, Counts = Counts.OrderBy(p => p.Key, StringComparer.Ordinal) });

    public SpreadsheetDryRunResult DryRun(Guid w, SpreadsheetSnapshot source)
    {
        var plan = new BudgetSpreadsheetMapper().Map(w, source); var items = plan.Items.ToList();
        void Invalid(string code, BootstrapDisposition disposition = BootstrapDisposition.Invalid) => items.Add(new("PREFLIGHT:" + code, SpreadsheetEntity.Validation,
            new("Destino"), "ReadOnlyDestinationSnapshot", Status: disposition, Reason: code));
        if (SchemaVersion != 1 || w != WorkspaceId || ReadAtUtc == default || !DatabaseReadOnly) Invalid("InvalidReadOnlyDestinationSnapshot");
        if (OwnerUserId is null || OwnerUserId == Guid.Empty || !OwnerExists || WorkspaceType != "Personal" || IsActive != true) Invalid("ActivePersonalAndPersistedOwnerRequired");
        var schemaReady = AppliedMigrations.Contains(BudgetSpreadsheetImporter.RequiredMigration, StringComparer.Ordinal);
        if (!schemaReady) Invalid("T09.1MigrationNotApplied:DoNotApplyAutomatically");
        var expected = Baseline.Split(' ').Concat(new[] { "financial_bootstrap_provenance", "financial_monthly_aggregate_targets" }).ToHashSet(StringComparer.Ordinal);
        // Recognize the new financial journal only when its schema migration is explicitly present.
        // Unknown tables, a missing expected journal or a nonempty journal still require direct preflight.
        if (AppliedMigrations.Contains("20261024004947_AddOfficialInvoiceConsolidation", StringComparer.Ordinal)) expected.Add("financial_credit_card_invoice_consolidations");
        // Item links of the Pluggy laboratory (T09.1 POC) are workspace-scoped and must be empty like every other table.
        if (AppliedMigrations.Contains("20261028121402_AddPluggyItemLinks", StringComparer.Ordinal)) expected.Add("pluggy_item_links");
        if (AppliedMigrations.Contains("20261028135409_AddPluggyTransactionImport", StringComparer.Ordinal))
            foreach (var table in new[] { "pluggy_sync_runs", "pluggy_imported_transactions", "pluggy_transaction_changes" }) expected.Add(table);
        if (Baseline.Split(' ').Any(t => !Counts.ContainsKey(t)) || Counts.Keys.Any(t => !expected.Contains(t)) || Counts.Values.Any(n => n < 0)) Invalid("IncompleteDestinationInventoryRequiresDirectPreflight");
        if (schemaReady && expected.Any(t => !Counts.ContainsKey(t))) Invalid("MissingT09.1TablesRequiresDirectPreflight");
        foreach (var p in Counts.Where(p => p.Value != 0 && p.Key != "financial_workspace_reset_audit"))
            Invalid("NonEmptyDestinationRequiresDirectProvenancePreflight:" + p.Key, BootstrapDisposition.Conflict);
        // No claiming AlreadyImported from names/counts. Nonempty targets use the database-backed inspector.
        var graph = SpreadsheetDomainGraph.Build(plan with { Items = items }, OwnerUserId ?? Guid.Empty, ReadAtUtc);
        var counts = new Dictionary<string, long>(Counts) { ["__migrations"] = AppliedMigrations.Length };
        foreach (var table in expected) counts.TryAdd(table, -1); // absent != known empty
        return new(w, OwnerUserId, plan.SourceHash, plan.PlanHash, source.ReadAtUtc, ReadAtUtc, DatabaseReadOnly,
            graph.Items, counts, "ReadOnlyDestinationSnapshot", ContentHash);
    }
}
