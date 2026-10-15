using Saldo360.Domain.CreditCards;

namespace Saldo360.Application.CreditCards;

public interface IFinancialCreditCardRepository
{
    Task AddAsync(FinancialCreditCard card, CancellationToken cancellationToken);
    Task<FinancialCreditCard?> FindAsync(Guid familyId, Guid cardId, CancellationToken cancellationToken);
    Task SaveChangesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<FinancialCreditCardDto>> ListAsync(Guid familyId, bool includeArchived, CancellationToken cancellationToken);
    Task<FinancialCreditCardDto?> GetAsync(Guid familyId, Guid cardId, CancellationToken cancellationToken);
    Task<bool> IsHolderOfFamilyAsync(Guid familyId, Guid userId, CancellationToken cancellationToken);
    Task<bool> IsActiveAccountOfFamilyAsync(Guid familyId, Guid accountId, CancellationToken cancellationToken);
}
