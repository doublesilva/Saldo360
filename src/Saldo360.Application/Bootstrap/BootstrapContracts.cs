namespace Saldo360.Application.Bootstrap;

/// <summary>Created means would-create in a dry-run, not a persisted write.</summary>
public enum BootstrapDisposition { Created, AlreadyImported, Unmapped, Conflict, Invalid }
public sealed record BootstrapSource(string SourceSystem, string SourceDocument, string ExternalReference, string? MetadataJson = null);
public sealed record BootstrapDryRunItem(string Aggregate, string ExternalReference, BootstrapDisposition Disposition, string? Code = null, bool ExcludedByApprovedDecision = false);
public sealed record BootstrapDryRunReport(Guid WorkspaceId, IReadOnlyList<BootstrapDryRunItem> Items)
{
    public bool ZeroWrite => true;
    // Only an explicitly approved exclusion may remain Unmapped without blocking the entire plan.
    // This is eligibility, not authorization to execute or evidence of a reviewed dry-run.
    public bool CanExecute => Items.All(x => x.Disposition is BootstrapDisposition.Created or BootstrapDisposition.AlreadyImported
        || x.Disposition == BootstrapDisposition.Unmapped && x.ExcludedByApprovedDecision);
    public IReadOnlyDictionary<BootstrapDisposition, int> Counts => Enum.GetValues<BootstrapDisposition>().ToDictionary(x => x, x => Items.Count(i => i.Disposition == x));
}
