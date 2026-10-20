using Saldo360.Domain.Common;

namespace Saldo360.Domain.CreditCards;

/// <summary>Associação explícita e reversível entre uma previsão de pagamento e uma fatura.</summary>
public sealed class FinancialPlannedInvoiceReconciliation : Entity
{
    private FinancialPlannedInvoiceReconciliation() { }

    public Guid WorkspaceId { get; private init; }
    public Guid PlannedTransactionId { get; private init; }
    public Guid CreditCardInvoiceId { get; private init; }
    public DateTimeOffset CreatedAt { get; private init; }
    public Guid CreatedByUserId { get; private init; }
    public DateTimeOffset? RemovedAt { get; private set; }
    public Guid? RemovedByUserId { get; private set; }

    public static FinancialPlannedInvoiceReconciliation Create(
        Guid workspaceId, Guid plannedTransactionId, Guid invoiceId, Guid userId, DateTimeOffset now) => new()
    {
        WorkspaceId = workspaceId,
        PlannedTransactionId = plannedTransactionId,
        CreditCardInvoiceId = invoiceId,
        CreatedAt = now,
        CreatedByUserId = userId,
    };

    public bool IsActive => RemovedAt is null;

    public bool Remove(Guid userId, DateTimeOffset now)
    {
        if (!IsActive) return false;
        RemovedAt = now;
        RemovedByUserId = userId;
        return true;
    }
}
