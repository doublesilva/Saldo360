using Saldo360.Domain.Common;

namespace Saldo360.Domain.Bootstrap;

/// <summary>Workspace-scoped source identity. The importer must save this and its target in the same transaction.</summary>
public sealed class FinancialBootstrapProvenance : Entity
{
    private FinancialBootstrapProvenance() { }
    public Guid WorkspaceId { get; private init; }
    public string SourceSystem { get; private init; } = "";
    public string SourceDocument { get; private init; } = "";
    public string ExternalReference { get; private init; } = "";
    public string ContentHash { get; private init; } = "";
    public string EntityType { get; private init; } = "";
    public Guid EntityId { get; private init; }
    public string? MetadataJson { get; private init; }
    public DateTimeOffset CreatedAt { get; private init; }

    public static FinancialBootstrapProvenance Create(Guid w, string system, string document, string reference, string hash, string entityType, Guid entityId, string? metadata, DateTimeOffset now)
    {
        static bool Valid(string value, int max) => !string.IsNullOrWhiteSpace(value) && value == value.Trim() && value.Length <= max;
        if (w == Guid.Empty || entityId == Guid.Empty || !Valid(system, 80) || !Valid(document, 200) || !Valid(reference, 200)
            || !Valid(entityType, 100) || hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new ArgumentException("Proveniência inválida.");
        if (metadata is not null) { using var parsed = System.Text.Json.JsonDocument.Parse(metadata); }
        return new() { WorkspaceId = w, SourceSystem = system, SourceDocument = document, ExternalReference = reference,
            ContentHash = hash.ToLowerInvariant(), EntityType = entityType, EntityId = entityId, MetadataJson = metadata, CreatedAt = now };
    }
}
