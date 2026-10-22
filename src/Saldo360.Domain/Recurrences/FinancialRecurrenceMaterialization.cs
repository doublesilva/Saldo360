using Saldo360.Domain.Common;

namespace Saldo360.Domain.Recurrences;

/// <summary>Identidade persistente entre uma ocorrência calculada e a previsão criada para ela.</summary>
public sealed class FinancialRecurrenceMaterialization : Entity
{
    private FinancialRecurrenceMaterialization() { }

    public Guid WorkspaceId { get; private init; }
    public Guid RecurrenceId { get; private init; }
    public int OccurrenceNumber { get; private init; }
    public DateOnly ScheduledDate { get; private init; }
    public Guid? PlannedTransactionId { get; private init; }
    public Guid? CreditCardPurchaseId { get; private init; }
    public DateTimeOffset CreatedAt { get; private init; }

    public static FinancialRecurrenceMaterialization Create(Guid workspaceId, Guid recurrenceId, int occurrenceNumber, DateOnly scheduledDate, Guid plannedTransactionId, DateTimeOffset createdAt) => new()
    {
        WorkspaceId = workspaceId, RecurrenceId = recurrenceId, OccurrenceNumber = occurrenceNumber,
        ScheduledDate = scheduledDate, PlannedTransactionId = plannedTransactionId, CreatedAt = createdAt,
    };

    public static FinancialRecurrenceMaterialization CreateCard(Guid workspaceId, Guid recurrenceId, int occurrenceNumber, DateOnly scheduledDate, Guid purchaseId, DateTimeOffset createdAt) => new()
    {
        WorkspaceId = workspaceId, RecurrenceId = recurrenceId, OccurrenceNumber = occurrenceNumber,
        ScheduledDate = scheduledDate, CreditCardPurchaseId = purchaseId, CreatedAt = createdAt,
    };
}
