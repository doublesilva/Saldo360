using Saldo360.Domain.CreditCards;

namespace Saldo360.Application.CreditCards;

public interface IFinancialCreditCardRepository
{
    Task AddAsync(FinancialCreditCard card, CancellationToken cancellationToken);
    Task<FinancialCreditCard?> FindAsync(Guid workspaceId, Guid cardId, CancellationToken cancellationToken);
    Task SaveChangesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<FinancialCreditCardDto>> ListAsync(Guid workspaceId, bool includeArchived, CancellationToken cancellationToken);
    Task<FinancialCreditCardDto?> GetAsync(Guid workspaceId, Guid cardId, CancellationToken cancellationToken);
    Task<bool> IsHolderOfWorkspaceAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken);
    Task<bool> IsActiveAccountOfWorkspaceAsync(Guid workspaceId, Guid accountId, CancellationToken cancellationToken);
}
