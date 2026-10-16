using Saldo360.Domain.Common;

namespace Saldo360.Domain.CreditCards;

public enum FinancialCreditCardInvoicePaymentStatus { Active, Cancelled }

public sealed class FinancialCreditCardInvoicePayment : Entity
{
    private FinancialCreditCardInvoicePayment() { }

    public Guid FamilyId { get; private init; }
    public Guid InvoiceId { get; private init; }
    public Guid FinancialAccountId { get; private init; }
    public Guid TransactionId { get; private init; }
    public Guid OperationId { get; private init; }
    public DateOnly PaymentDate { get; private init; }
    public decimal Amount { get; private init; }
    public DateTimeOffset CreatedAt { get; private init; }
    public Guid CreatedByUserId { get; private init; }
    public FinancialCreditCardInvoicePaymentStatus Status { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public Guid? CancelledByUserId { get; private set; }

    public static FinancialCreditCardInvoicePayment Create(Guid familyId, Guid invoiceId, Guid accountId, Guid transactionId, Guid operationId, DateOnly paymentDate, decimal amount, Guid userId, DateTimeOffset now) => new()
    {
        FamilyId = familyId, InvoiceId = invoiceId, FinancialAccountId = accountId, TransactionId = transactionId, OperationId = operationId,
        PaymentDate = paymentDate, Amount = amount, CreatedAt = now, CreatedByUserId = userId,
        Status = FinancialCreditCardInvoicePaymentStatus.Active,
    };

    public bool Cancel(Guid userId, DateTimeOffset now)
    {
        if (Status == FinancialCreditCardInvoicePaymentStatus.Cancelled) return false;
        Status = FinancialCreditCardInvoicePaymentStatus.Cancelled;
        CancelledAt = now; CancelledByUserId = userId;
        return true;
    }
}
