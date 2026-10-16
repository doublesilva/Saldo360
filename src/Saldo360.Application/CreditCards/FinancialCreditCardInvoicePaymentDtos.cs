using Saldo360.Domain.CreditCards;

namespace Saldo360.Application.CreditCards;

public sealed record FinancialCreditCardInvoicePaymentDto(Guid Id, Guid InvoiceId, Guid FinancialAccountId, Guid TransactionId, Guid OperationId, DateOnly PaymentDate, string Amount, FinancialCreditCardInvoicePaymentStatus Status, DateTimeOffset CreatedAt, DateTimeOffset? CancelledAt);
public sealed record FinancialCreditCardInvoicePaymentInput(Guid? OperationId, Guid? FinancialAccountId, DateOnly? PaymentDate, decimal? Amount);
public enum FinancialCreditCardInvoicePaymentAccess { Allowed, NotFound, Invalid, Conflict }
public sealed record FinancialCreditCardInvoicePaymentResult(FinancialCreditCardInvoicePaymentAccess Access, FinancialCreditCardInvoicePaymentDto? Payment, IDictionary<string,string[]>? Errors, string? Message = null);
