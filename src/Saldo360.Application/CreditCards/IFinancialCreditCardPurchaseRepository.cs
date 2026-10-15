using Saldo360.Domain.CreditCards;

namespace Saldo360.Application.CreditCards;

public interface IFinancialCreditCardPurchaseRepository
{
    Task AddAsync(FinancialCreditCardPurchase purchase, CancellationToken cancellationToken);
    Task<FinancialCreditCardPurchase?> FindAsync(Guid familyId, Guid cardId, Guid purchaseId, CancellationToken cancellationToken);
    Task<FinancialCreditCardPurchaseDto?> GetAsync(Guid familyId, Guid cardId, Guid purchaseId, CancellationToken cancellationToken);
    Task<IReadOnlyList<FinancialCreditCardPurchaseDto>> ListAsync(Guid familyId, Guid cardId, DateOnly? from, DateOnly? through, CancellationToken cancellationToken);
    Task SaveChangesAsync(CancellationToken cancellationToken);
    Task<CreditCardCalendarConfig?> GetCardCalendarAsync(Guid familyId, Guid cardId, CancellationToken cancellationToken);
    Task<FinancialCreditCardInvoice?> FindInvoiceAsync(Guid familyId, Guid cardId, DateOnly closingDate, CancellationToken cancellationToken);
    Task AddInvoiceAsync(FinancialCreditCardInvoice invoice, CancellationToken cancellationToken);
    Task<IReadOnlyList<FinancialCreditCardInvoiceDto>> ListInvoicesAsync(Guid familyId, Guid cardId, string from, string through, DateOnly today, CancellationToken cancellationToken);
    Task<FinancialCreditCardInvoiceDto?> GetInvoiceAsync(Guid familyId, Guid cardId, Guid invoiceId, DateOnly today, CancellationToken cancellationToken);
    Task<bool> IsActiveCardAsync(Guid familyId, Guid cardId, CancellationToken cancellationToken);
    Task<bool> IsExpenseCategoryAsync(Guid familyId, Guid categoryId, CancellationToken cancellationToken);
}

public sealed record CreditCardCalendarConfig(int ClosingDay, int DueDay);
