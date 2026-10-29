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
        await WithOriginAsync(workspaceId, await db.FinancialCreditCards.AsNoTracking().Where(card => card.WorkspaceId == workspaceId && (includeArchived || !card.IsArchived))
            .OrderBy(card => card.Name).ToListAsync(cancellationToken), cancellationToken);

    public async Task<FinancialCreditCardDto?> GetAsync(Guid workspaceId, Guid cardId, CancellationToken cancellationToken)
    {
        var card = await db.FinancialCreditCards.AsNoTracking().SingleOrDefaultAsync(card => card.WorkspaceId == workspaceId && card.Id == cardId, cancellationToken);
        return card is null ? null : (await WithOriginAsync(workspaceId, [card], cancellationToken))[0];
    }

    // Origem e escopo do cartão são derivados do vínculo Pluggy: o escopo (PF/PJ) é o das contas bancárias da MESMA conexão. Cartão manual, ou conexão
    // sem conta bancária de escopo único, fica sem escopo (nunca é inventado). A conta pagadora é uma preferência e não altera este escopo.
    private async Task<IReadOnlyList<FinancialCreditCardDto>> WithOriginAsync(Guid workspaceId, IReadOnlyList<FinancialCreditCard> cards, CancellationToken cancellationToken)
    {
        var ids = cards.Select(card => card.Id).ToList();
        var links = await db.PluggyAccountLinks.AsNoTracking()
            .Where(link => link.WorkspaceId == workspaceId && link.CreditCardId != null && ids.Contains(link.CreditCardId.Value))
            .Select(link => new { CardId = link.CreditCardId!.Value, link.ConnectionId }).ToListAsync(cancellationToken);
        var connections = links.Select(link => link.ConnectionId).Distinct().ToList();
        var scopes = connections.Count == 0 ? [] : await (from link in db.PluggyAccountLinks.AsNoTracking()
                                                            join account in db.FinancialAccounts.AsNoTracking() on link.FinancialAccountId equals account.Id
                                                            where link.WorkspaceId == workspaceId && connections.Contains(link.ConnectionId) && account.WorkspaceId == workspaceId
                                                            select new { link.ConnectionId, account.Scope }).ToListAsync(cancellationToken);
        return cards.Select(card =>
        {
            var link = links.FirstOrDefault(item => item.CardId == card.Id);
            if (link is null) return ToDto(card);
            var distinct = scopes.Where(item => item.ConnectionId == link.ConnectionId).Select(item => item.Scope).Distinct().ToList();
            return ToDto(card) with { Origin = "Pluggy", Scope = distinct.Count == 1 ? distinct[0].ToString() : null };
        }).ToList();
    }

    public Task<bool> IsHolderOfWorkspaceAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken) =>
        db.FinancialWorkspaces.AnyAsync(member => member.Id == workspaceId && member.OwnerUserId == userId && member.IsActive && member.Type == Saldo360.Domain.Workspaces.FinancialWorkspaceType.Personal, cancellationToken);

    public Task<bool> IsActiveAccountOfWorkspaceAsync(Guid workspaceId, Guid accountId, CancellationToken cancellationToken) =>
        db.FinancialAccounts.AnyAsync(account => account.WorkspaceId == workspaceId && account.Id == accountId && account.IsActive
            && (account.Type == Saldo360.Domain.Accounts.AccountType.Checking || account.Type == Saldo360.Domain.Accounts.AccountType.Savings), cancellationToken);

    public Task<CreditCardPdfContext?> GetAsync(Guid workspaceId, Guid cardId, Guid userId, CancellationToken cancellationToken) =>
        (from card in db.FinancialCreditCards
         where card.WorkspaceId == workspaceId && card.Id == cardId && !card.IsArchived
             && db.FinancialWorkspaces.Any(member => member.Id == workspaceId && member.OwnerUserId == userId && member.IsActive && member.Type == Saldo360.Domain.Workspaces.FinancialWorkspaceType.Personal)
         select new CreditCardPdfContext(card.Issuer, card.HolderUserId, card.LastFourDigits))
        .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<CreditCardStatementCandidate>> ListActiveAsync(Guid workspaceId, CancellationToken cancellationToken) =>
        await db.FinancialCreditCards.AsNoTracking()
            .Where(card => card.WorkspaceId == workspaceId && !card.IsArchived && card.LastFourDigits != null)
            .Select(card => new CreditCardStatementCandidate(card.Id, card.Issuer, card.LastFourDigits!, card.HolderUserId,
                db.Users.Where(user => user.Id == card.HolderUserId).Select(user => user.DisplayName).FirstOrDefault()))
            .ToListAsync(cancellationToken);

    private static FinancialCreditCardDto ToDto(FinancialCreditCard card) => new(
        card.Id, card.WorkspaceId, card.Name, card.Issuer, card.LastFourDigits, card.HolderUserId,
        Domain.Common.Money.Format(card.CreditLimit), card.ClosingDay, card.DueDay, card.DefaultPaymentAccountId,
        card.IsArchived, card.CreatedAt, card.UpdatedAt, card.ArchivedAt, card.GmailQuery);
}
