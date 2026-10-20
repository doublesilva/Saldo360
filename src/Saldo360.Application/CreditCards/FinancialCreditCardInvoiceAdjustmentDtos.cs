namespace Saldo360.Application.CreditCards;

public sealed record FinancialCreditCardInvoiceAdjustmentInput(
    string Type,
    string Nature,
    string TransactionDate,
    string Description,
    string Amount,
    string ExternalId,
    Guid? CategoryId);

public sealed record FinancialCreditCardInvoiceAdjustmentDto(
    Guid Id,
    Guid InvoiceId,
    string Type,
    string Nature,
    string TransactionDate,
    string Description,
    string Amount,
    string ExternalId,
    Guid? CategoryId,
    string Status,
    string CreatedAt,
    string? CancelledAt);

public enum InvoiceAdjustmentAccess { Allowed, NotFound, Invalid, Conflict, PaidTotalConflict }

public sealed record InvoiceAdjustmentOperationResult(
    InvoiceAdjustmentAccess Access,
    FinancialCreditCardInvoiceAdjustmentDto? Adjustment = null,
    IDictionary<string, string[]>? Errors = null,
    string? Message = null);

public interface IFinancialCreditCardInvoiceAdjustmentRepository
{
    Task<InvoiceAdjustmentOperationResult> CreateAsync(
        Guid workspaceId, Guid cardId, Guid invoiceId, Guid userId,
        FinancialCreditCardInvoiceAdjustmentInput input, DateTimeOffset now,
        CancellationToken cancellationToken);

    Task<InvoiceAdjustmentOperationResult> CancelAsync(
        Guid workspaceId, Guid cardId, Guid invoiceId, Guid adjustmentId,
        Guid userId, DateTimeOffset now, CancellationToken cancellationToken);

    Task<IReadOnlyList<FinancialCreditCardInvoiceAdjustmentDto>?> ListAsync(
        Guid workspaceId, Guid cardId, Guid invoiceId, CancellationToken cancellationToken);
}
