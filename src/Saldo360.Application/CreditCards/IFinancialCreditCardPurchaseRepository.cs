using Saldo360.Domain.CreditCards;

namespace Saldo360.Application.CreditCards;

public interface IFinancialCreditCardPurchaseRepository
{
    Task AddAsync(FinancialCreditCardPurchase purchase, CancellationToken cancellationToken);
    Task<FinancialCreditCardPurchase?> FindAsync(Guid familyId, Guid cardId, Guid purchaseId, CancellationToken cancellationToken);
    Task<FinancialCreditCardPurchaseDto?> GetAsync(Guid familyId, Guid cardId, Guid purchaseId, CancellationToken cancellationToken);
    Task<IReadOnlyList<FinancialCreditCardPurchaseDto>> ListAsync(Guid familyId, Guid cardId, DateOnly? from, DateOnly? through, CancellationToken cancellationToken);
    Task SaveChangesAsync(CancellationToken cancellationToken);
    Task<bool> IsActiveCardAsync(Guid familyId, Guid cardId, CancellationToken cancellationToken);
    Task<bool> IsExpenseCategoryAsync(Guid familyId, Guid categoryId, CancellationToken cancellationToken);
}
