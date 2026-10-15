namespace Saldo360.Application.CreditCards;

public sealed record FinancialPlannedInvoiceReconciliationDto(
    Guid Id,
    Guid FamilyId,
    Guid PlannedTransactionId,
    Guid CreditCardInvoiceId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RemovedAt);

public sealed record FinancialPlannedInvoiceReconciliationInput(Guid? PlannedTransactionId, Guid? CreditCardInvoiceId);

public enum FinancialPlannedInvoiceReconciliationAccess { Allowed, NotFound, Invalid, Conflict }

public sealed record FinancialPlannedInvoiceReconciliationResult(
    FinancialPlannedInvoiceReconciliationAccess Access,
    FinancialPlannedInvoiceReconciliationDto? Reconciliation,
    IDictionary<string, string[]>? Errors,
    string? Message = null);
