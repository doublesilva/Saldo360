namespace Saldo360.Application.CreditCards;

public interface IFinancialPlannedInvoiceReconciliationRepository
{
    Task<IReadOnlyList<FinancialPlannedInvoiceReconciliationDto>?> ListAsync(Guid familyId, Guid? invoiceId, Guid? plannedTransactionId, CancellationToken cancellationToken);
    Task<FinancialPlannedInvoiceReconciliationDto?> GetAsync(Guid familyId, Guid id, CancellationToken cancellationToken);
    Task<FinancialPlannedInvoiceReconciliationDto?> FindActiveAsync(Guid familyId, Guid plannedTransactionId, Guid invoiceId, CancellationToken cancellationToken);
    Task<ReconciliationEligibility?> GetEligibilityAsync(Guid familyId, Guid plannedTransactionId, Guid invoiceId, CancellationToken cancellationToken);
    Task<FinancialPlannedInvoiceReconciliationDto?> CreateAsync(Guid familyId, Guid plannedTransactionId, Guid invoiceId, Guid userId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> RemoveAsync(Guid familyId, Guid id, Guid userId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> HasActiveForCardAsync(Guid familyId, Guid cardId, CancellationToken cancellationToken);
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
