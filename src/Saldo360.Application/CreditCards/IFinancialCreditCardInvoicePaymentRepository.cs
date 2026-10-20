using Saldo360.Domain.CreditCards;

namespace Saldo360.Application.CreditCards;

public interface IFinancialCreditCardInvoicePaymentRepository
{
    Task<InvoicePaymentSnapshot?> GetSnapshotAsync(Guid workspaceId, Guid cardId, Guid invoiceId, DateOnly today, CancellationToken cancellationToken);
    Task<IReadOnlyList<FinancialCreditCardInvoicePaymentDto>?> ListAsync(Guid workspaceId, Guid cardId, Guid invoiceId, CancellationToken cancellationToken);
    Task<FinancialCreditCardInvoicePaymentDto?> GetAsync(Guid workspaceId, Guid cardId, Guid invoiceId, Guid paymentId, CancellationToken cancellationToken);
    Task<FinancialCreditCardInvoicePaymentDto?> GetByOperationAsync(Guid workspaceId, Guid operationId, CancellationToken cancellationToken);
    Task<bool> PayloadMatchesAsync(Guid workspaceId, Guid operationId, Guid invoiceId, Guid accountId, DateOnly paymentDate, decimal amount, CancellationToken cancellationToken);
    Task<PaymentCreationResult> CreateAsync(Guid workspaceId, Guid cardId, Guid invoiceId, Guid operationId, Guid accountId, DateOnly paymentDate, decimal amount, Guid userId, DateOnly today, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> CancelAsync(Guid workspaceId, Guid cardId, Guid invoiceId, Guid paymentId, Guid userId, DateTimeOffset now, CancellationToken cancellationToken);
}

public sealed record InvoicePaymentSnapshot(Guid InvoiceId, DateOnly ClosingDate, decimal TotalAmount, decimal PaidAmount, Guid? DefaultPaymentAccountId, bool AccountExists, bool AccountActive);
public enum PaymentOperationResult { Success, NotFound, OpenInvoice, InvalidAccount, ExceedsDue, DuplicateOperation, Conflict }
public sealed record PaymentCreationResult(PaymentOperationResult Result, Guid? PaymentId = null);
