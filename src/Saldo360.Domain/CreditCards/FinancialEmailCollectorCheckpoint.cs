using Saldo360.Domain.Common;

namespace Saldo360.Domain.CreditCards;

public sealed class FinancialEmailCollectorCheckpoint : Entity
{
    private FinancialEmailCollectorCheckpoint() { ProfileName = string.Empty; }
    public Guid WorkspaceId { get; private init; }
    public string ProfileName { get; private init; }
    public DateTimeOffset? LastMessageAt { get; private set; }
    public DateTimeOffset? LastStartedAt { get; private set; }
    public DateTimeOffset? LastCompletedAt { get; private set; }
    public DateTimeOffset? LastSuccessfulRunAt { get; private set; }
    public long LastDurationMilliseconds { get; private set; }
    public string? LastResult { get; private set; }
    public int LastMessagesScanned { get; private set; }
    public int LastCandidateAttachments { get; private set; }
    public int LastAcceptedDocuments { get; private set; }
    public int LastDuplicates { get; private set; }
    public int LastNeedsReview { get; private set; }
    public int LastFailures { get; private set; }
    public static FinancialEmailCollectorCheckpoint Create(Guid workspaceId, string profileName) =>
        new() { Id = Guid.NewGuid(), WorkspaceId = workspaceId, ProfileName = profileName };
    public void Start(DateTimeOffset now) => LastStartedAt = now;
    public void Complete(DateTimeOffset now, DateTimeOffset? newestMessageAt, string result, long durationMilliseconds,
        int messages, int candidates, int accepted, int duplicates, int needsReview, int failures)
    {
        LastCompletedAt = now; LastResult = result; LastDurationMilliseconds = Math.Max(0, durationMilliseconds);
        LastMessagesScanned = messages; LastCandidateAttachments = candidates; LastAcceptedDocuments = accepted;
        LastDuplicates = duplicates; LastNeedsReview = needsReview; LastFailures = failures;
        if (failures == 0) LastSuccessfulRunAt = now;
        if (newestMessageAt is { } candidate && (LastMessageAt is null || candidate > LastMessageAt)) LastMessageAt = candidate;
    }
    public void Fail(DateTimeOffset now, long durationMilliseconds)
    {
        LastCompletedAt = now; LastResult = "Failed"; LastDurationMilliseconds = Math.Max(0, durationMilliseconds); LastFailures = 1;
    }
}
