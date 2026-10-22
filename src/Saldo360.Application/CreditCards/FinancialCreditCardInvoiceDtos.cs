using Saldo360.Domain.CreditCards;

namespace Saldo360.Application.CreditCards;

public sealed record FinancialCreditCardInvoiceInstallmentDto(Guid InstallmentId, Guid PurchaseId, string PurchaseDescription, int? InstallmentNumber, int? InstallmentCount, string Amount);
public enum InvoicePaymentStatus { Unpaid, PartiallyPaid, Paid }
public sealed record FinancialCreditCardInvoiceDto(Guid Id, Guid CreditCardId, string Competence, DateOnly CycleStartDate, DateOnly ClosingDate, DateOnly DueDate, FinancialCreditCardInvoiceStatus Status, string TotalAmount, string PaidAmount, string RemainingAmount, InvoicePaymentStatus PaymentStatus, IReadOnlyList<FinancialCreditCardInvoiceInstallmentDto> Installments, int InstallmentCount = 0);
public sealed record FinancialCreditCardLimitDto(string CreditLimit, string UsedLimit, string AvailableLimit, string OverLimitAmount);
