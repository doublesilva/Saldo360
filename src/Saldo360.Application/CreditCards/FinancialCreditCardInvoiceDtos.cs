using Saldo360.Domain.CreditCards;

namespace Saldo360.Application.CreditCards;

public sealed record FinancialCreditCardInvoiceInstallmentDto(Guid InstallmentId, Guid PurchaseId, string PurchaseDescription, int InstallmentNumber, int InstallmentCount, string Amount);
public sealed record FinancialCreditCardInvoiceDto(Guid Id, Guid CreditCardId, string Competence, DateOnly CycleStartDate, DateOnly ClosingDate, DateOnly DueDate, FinancialCreditCardInvoiceStatus Status, string TotalAmount, IReadOnlyList<FinancialCreditCardInvoiceInstallmentDto> Installments);
