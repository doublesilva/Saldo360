namespace Saldo360.Application.Workspaces;

public sealed record FinancialResetReport(Guid WorkspaceId, string Status, IReadOnlyDictionary<string, long> Counts);
public interface IFinancialWorkspaceResetService
{
    Task<FinancialResetReport> PreviewAsync(Guid workspaceId, Guid actorUserId, CancellationToken ct);
    Task<FinancialResetReport> ResetAsync(Guid workspaceId, Guid actorUserId, string confirmation, string traceId, CancellationToken ct);
}
public sealed class FinancialResetRejected(string code) : Exception(code) { public string Code { get; } = code; }

// Captured before asynchronous financial work. DB guards reject writes overtaken by reset.
public static class FinancialWriteScope
{
    public sealed class Context(Guid? workspaceId)
    {
        public Guid? WorkspaceId { get; } = workspaceId;
        public DateTimeOffset? DatabaseStartedAt { get; set; }
        public long? ExpectedVersion { get; set; }
    }
    private static readonly AsyncLocal<Context?> current = new();
    public static Context? Current => current.Value;
    public static IDisposable Enter(Guid? workspaceId)
    {
        var previous = current.Value;
        current.Value = new(workspaceId);
        return new Restore(previous);
    }
    private sealed class Restore(Context? previous) : IDisposable { public void Dispose() => current.Value = previous; }
    public static IDisposable EnterOperation(Guid? workspaceId) => current.Value is null ? Enter(workspaceId) : new Noop();
    private sealed class Noop : IDisposable { public void Dispose() { } }
}
