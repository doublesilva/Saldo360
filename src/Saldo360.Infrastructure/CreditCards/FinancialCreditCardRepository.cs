using Microsoft.EntityFrameworkCore;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.CreditCards;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.CreditCards;

internal sealed class FinancialCreditCardRepository(Saldo360DbContext db) : IFinancialCreditCardRepository, ICreditCardPdfContextRepository, ICreditCardStatementResolutionRepository
{
    public async Task AddAsync(FinancialCreditCard card, CancellationToken cancellationToken)
    {
        db.FinancialCreditCards.Add(card);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<FinancialCreditCard?> FindAsync(Guid workspaceId, Guid cardId, CancellationToken cancellationToken) =>
        db.FinancialCreditCards.SingleOrDefaultAsync(card => card.WorkspaceId == workspaceId && card.Id == cardId, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);

    public async Task<IReadOnlyList<FinancialCreditCardDto>> ListAsync(Guid workspaceId, bool includeArchived, CancellationToken cancellationToken) =>
        (await db.FinancialCreditCards.AsNoTracking().Where(card => card.WorkspaceId == workspaceId && (includeArchived || !card.IsArchived))
            .OrderBy(card => card.Name).ToListAsync(cancellationToken)).Select(ToDto).ToList();

    public async Task<FinancialCreditCardDto?> GetAsync(Guid workspaceId, Guid cardId, CancellationToken cancellationToken)
    {
        var card = await db.FinancialCreditCards.AsNoTracking().SingleOrDefaultAsync(card => card.WorkspaceId == workspaceId && card.Id == cardId, cancellationToken);
        return card is null ? null : ToDto(card);
    }

    public Task<bool> IsHolderOfWorkspaceAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken) =>
        db.FinancialWorkspaces.AnyAsync(member => member.Id == workspaceId && member.OwnerUserId == userId && member.IsActive && member.Type == Saldo360.Domain.Workspaces.FinancialWorkspaceType.Personal, cancellationToken);

    public Task<bool> IsActiveAccountOfWorkspaceAsync(Guid workspaceId, Guid accountId, CancellationToken cancellationToken) =>
        db.FinancialAccounts.AnyAsync(account => account.WorkspaceId == workspaceId && account.Id == accountId && account.IsActive, cancellationToken);

    public Task<CreditCardPdfContext?> GetAsync(Guid workspaceId, Guid cardId, Guid userId, CancellationToken cancellationToken) =>
        (from card in db.FinancialCreditCards
         where card.WorkspaceId == workspaceId && card.Id == cardId && !card.IsArchived
             && db.FinancialWorkspaces.Any(member => member.Id == workspaceId && member.OwnerUserId == userId && member.IsActive && member.Type == Saldo360.Domain.Workspaces.FinancialWorkspaceType.Personal)
         select new CreditCardPdfContext(card.Issuer, card.HolderUserId, card.LastFourDigits))
        .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<CreditCardStatementCandidate>> ListActiveAsync(Guid workspaceId, CancellationToken cancellationToken) =>
        await db.FinancialCreditCards.AsNoTracking()
            .Where(card => card.WorkspaceId == workspaceId && !card.IsArchived && card.LastFourDigits != null)
            .Select(card => new CreditCardStatementCandidate(card.Id, card.Issuer, card.LastFourDigits!, card.HolderUserId))
            .ToListAsync(cancellationToken);

    private static FinancialCreditCardDto ToDto(FinancialCreditCard card) => new(
        card.Id, card.WorkspaceId, card.Name, card.Issuer, card.LastFourDigits, card.HolderUserId,
        Domain.Common.Money.Format(card.CreditLimit), card.ClosingDay, card.DueDay, card.DefaultPaymentAccountId,
        card.IsArchived, card.CreatedAt, card.UpdatedAt, card.ArchivedAt);
}
