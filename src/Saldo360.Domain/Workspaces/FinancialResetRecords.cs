namespace Saldo360.Domain.Workspaces;

// Operational records survive financial reset; never contain financial values or descriptions.
public sealed class FinancialResetAudit
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ActorUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string TraceId { get; set; } = "";
    public long DeletedEntities { get; set; }
}
public sealed class FinancialResetFileCleanup
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string StorageReference { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public int Attempts { get; set; }
}
