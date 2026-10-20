namespace Saldo360.Application.CreditCards;

public interface IFinancialPlannedInvoiceReconciliationRepository
{
    Task<IReadOnlyList<FinancialPlannedInvoiceReconciliationDto>?> ListAsync(Guid workspaceId, Guid? invoiceId, Guid? plannedTransactionId, CancellationToken cancellationToken);
    Task<FinancialPlannedInvoiceReconciliationDto?> GetAsync(Guid workspaceId, Guid id, CancellationToken cancellationToken);
    Task<FinancialPlannedInvoiceReconciliationDto?> FindActiveAsync(Guid workspaceId, Guid plannedTransactionId, Guid invoiceId, CancellationToken cancellationToken);
    Task<ReconciliationEligibility?> GetEligibilityAsync(Guid workspaceId, Guid plannedTransactionId, Guid invoiceId, CancellationToken cancellationToken);
    Task<FinancialPlannedInvoiceReconciliationDto?> CreateAsync(Guid workspaceId, Guid plannedTransactionId, Guid invoiceId, Guid userId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> RemoveAsync(Guid workspaceId, Guid id, Guid userId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> HasActiveForCardAsync(Guid workspaceId, Guid cardId, CancellationToken cancellationToken);
}

public sealed record ReconciliationEligibility(
    bool PlannedExists,
    bool InvoiceExists,
    bool PlannedEligible,
    bool InvoiceEligible,
    bool AccountCompatible,
    bool PlannedAlreadyLinked,
    bool InvoiceAlreadyLinked,
    Guid PlannedAccountId,
    Guid? CardPaymentAccountId);
