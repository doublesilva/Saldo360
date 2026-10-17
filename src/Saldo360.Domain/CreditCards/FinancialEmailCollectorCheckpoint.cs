using Saldo360.Domain.Common;

namespace Saldo360.Domain.CreditCards;

public sealed class FinancialEmailCollectorCheckpoint : Entity
{
    private FinancialEmailCollectorCheckpoint() { ProfileName = string.Empty; }
    public Guid FamilyId { get; private init; }
    public string ProfileName { get; private init; }
    public DateTimeOffset? LastMessageAt { get; private set; }
    public DateTimeOffset? LastStartedAt { get; private set; }
    public DateTimeOffset? LastCompletedAt { get; private set; }
    public int LastMessagesScanned { get; private set; }
    public int LastAcceptedDocuments { get; private set; }
    public int LastFailures { get; private set; }
    public static FinancialEmailCollectorCheckpoint Create(Guid familyId, string profileName) =>
        new() { Id = Guid.NewGuid(), FamilyId = familyId, ProfileName = profileName };
    public void Start(DateTimeOffset now) => LastStartedAt = now;
    public void Complete(DateTimeOffset now, DateTimeOffset? newestMessageAt, int messages, int accepted, int failures)
    {
        LastCompletedAt = now; LastMessagesScanned = messages; LastAcceptedDocuments = accepted; LastFailures = failures;
        if (newestMessageAt is { } candidate && (LastMessageAt is null || candidate > LastMessageAt)) LastMessageAt = candidate;
    }
}
