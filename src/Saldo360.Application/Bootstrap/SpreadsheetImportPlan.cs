using System.Text.Json.Serialization;
using Saldo360.Domain.Budgets;

namespace Saldo360.Application.Bootstrap;

public enum SpreadsheetEntity { Category, Account, Card, AccountRecurrence, CardRecurrence, PlannedTransaction,
    Purchase, Installment, Invoice, KnownInstallment, MonthlyAggregateTarget, Exclusion, Parameter, Validation }
public sealed record SpreadsheetPayload(string Name, decimal? Amount = null, DateOnly? Date = null,
    string? Account = null, string? Card = null, string? Category = null, string? Institution = null,
    string? Scope = null, DateOnly? EndDate = null, int? Day = null, int? Count = null, int? Number = null,
    DateOnly? Competence = null, bool IsEstimated = false, MonthlyAggregateTargetType? TargetType = null,
    string? Purchase = null, string? Invoice = null);
public sealed record SpreadsheetPlanItem(string ExternalReference, SpreadsheetEntity EntityType,
    SpreadsheetPayload Payload, string SourceLocation, string? MetadataJson = null,
    BootstrapDisposition Status = BootstrapDisposition.Created, string Reason = "CreatePlanned",
    bool ExcludedByApprovedDecision = false, string[]? Warnings = null)
{
    public bool IsEntity => EntityType is not (SpreadsheetEntity.Exclusion or SpreadsheetEntity.Parameter or SpreadsheetEntity.Validation);
    public string ContentHash => BootstrapContentFingerprint.Compute(EntityType.ToString(), new { Payload, MetadataJson });
    public string IntendedAction => Status switch { BootstrapDisposition.Created => "Create", BootstrapDisposition.AlreadyImported => "Skip",
        BootstrapDisposition.Unmapped => "Exclude", _ => "Blocked" };
    public BootstrapSource Source => new(SpreadsheetSnapshot.System, SpreadsheetSnapshot.Document, ExternalReference, MetadataJson);
}
public sealed record SpreadsheetImportPlan(Guid WorkspaceId, string SourceHash, DateTimeOffset SourceReadAtUtc,
    IReadOnlyList<SpreadsheetPlanItem> Items)
{
    public string PlanHash => SpreadsheetSnapshot.Hash(new { WorkspaceId, SourceHash, MapperVersion = 2,
        Items = Items.Select(i => new { i.ExternalReference, i.EntityType, i.ContentHash, i.SourceLocation, i.Status, i.Reason, i.ExcludedByApprovedDecision }) });
}
public sealed record SpreadsheetDryRunResult(Guid WorkspaceId, Guid? OwnerUserId, string SourceHash, string PlanHash,
    DateTimeOffset SourceReadAtUtc, DateTimeOffset DestinationReadAtUtc, bool DatabaseReadOnly,
    IReadOnlyList<SpreadsheetPlanItem> Items, IReadOnlyDictionary<string, long> DestinationCounts,
    string DestinationMode = "DirectDatabase", string? DestinationSnapshotHash = null)
{
    public bool ZeroWrite => true;
    public int WarningCount => Items.Sum(i => i.Warnings?.Length ?? 0);
    public bool CanExecute => DatabaseReadOnly && PermitsExecution(Items);
    public static bool PermitsExecution(IEnumerable<SpreadsheetPlanItem> items) => items.All(i => i.Status is BootstrapDisposition.Created or BootstrapDisposition.AlreadyImported
        || i.Status == BootstrapDisposition.Unmapped && i.ExcludedByApprovedDecision);
    public IReadOnlyDictionary<string, int> CountsByStatus => Enum.GetValues<BootstrapDisposition>().ToDictionary(x => x.ToString(), x => Items.Count(i => i.Status == x));
    public IReadOnlyDictionary<string, int> CountsByAggregate => Enum.GetValues<SpreadsheetEntity>().Where(x => x < SpreadsheetEntity.Exclusion)
        .ToDictionary(x => x.ToString(), x => Items.Count(i => i.EntityType == x));
    public int ProvenanceRecords => Items.Count(i => i.IsEntity);
    public int PlannedNewProvenanceRecords => Items.Count(i => i.IsEntity && i.Status == BootstrapDisposition.Created);
    public string ReportHash => SpreadsheetSnapshot.Hash(new { WorkspaceId, OwnerUserId, SourceHash, PlanHash, SourceReadAtUtc, DestinationReadAtUtc,
        DatabaseReadOnly, DestinationMode, DestinationSnapshotHash, Items, DestinationCounts });
}
