using Saldo360.Application.Accounts;
using Saldo360.Application.Workspaces;
using Saldo360.Domain.CreditCards;

namespace Saldo360.Application.CreditCards;

public sealed class FinancialCreditCardService(
    IFinancialWorkspaceRepository workspaces,
    IFinancialCreditCardRepository cards,
    TimeProvider timeProvider)
{
    public async Task<CreditCardResult> CreateAsync(Guid workspaceId, Guid userId, CreditCardInput input, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(workspaceId, userId, cancellationToken)) return CreditCardResult.NotFound;
        var (details, errors) = await ValidateAsync(workspaceId, input, cancellationToken);
        if (details is null) return CreditCardResult.Invalid(errors);
        var card = FinancialCreditCard.Create(workspaceId, details, userId, timeProvider.GetUtcNow());
        await cards.AddAsync(card, cancellationToken);
        return CreditCardResult.Ok((await cards.GetAsync(workspaceId, card.Id, cancellationToken))!);
    }

    public async Task<IReadOnlyList<FinancialCreditCardDto>?> ListAsync(Guid workspaceId, Guid userId, bool includeArchived, CancellationToken cancellationToken) =>
        await IsParticipantAsync(workspaceId, userId, cancellationToken) ? await cards.ListAsync(workspaceId, includeArchived, cancellationToken) : null;

    public async Task<FinancialCreditCardDto?> GetAsync(Guid workspaceId, Guid cardId, Guid userId, CancellationToken cancellationToken) =>
        await IsParticipantAsync(workspaceId, userId, cancellationToken) ? await cards.GetAsync(workspaceId, cardId, cancellationToken) : null;

    public async Task<CreditCardResult> UpdateAsync(Guid workspaceId, Guid cardId, Guid userId, CreditCardInput input, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(workspaceId, userId, cancellationToken) || await cards.FindAsync(workspaceId, cardId, cancellationToken) is not { } card)
            return CreditCardResult.NotFound;
        if (card.IsArchived) return CreditCardResult.Invalid(new Dictionary<string, string[]> { ["general"] = ["Cartão arquivado não pode ser alterado."] });
        var (details, errors) = await ValidateAsync(workspaceId, input, cancellationToken);
        if (details is null) return CreditCardResult.Invalid(errors);
        card.Update(details, userId, timeProvider.GetUtcNow());
        await cards.SaveChangesAsync(cancellationToken);
        return CreditCardResult.Ok((await cards.GetAsync(workspaceId, card.Id, cancellationToken))!);
    }

    public async Task<bool> ArchiveAsync(Guid workspaceId, Guid cardId, Guid userId, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(workspaceId, userId, cancellationToken) || await cards.FindAsync(workspaceId, cardId, cancellationToken) is not { } card) return false;
        card.Archive(userId, timeProvider.GetUtcNow());
        await cards.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<(CreditCardDetails? Details, Dictionary<string, string[]> Errors)> ValidateAsync(Guid workspaceId, CreditCardInput input, CancellationToken cancellationToken)
    {
        var details = CreditCardDetails.TryCreate(input.Name, input.Issuer, input.LastFourDigits, input.HolderUserId, input.CreditLimit, input.ClosingDay, input.DueDay, input.DefaultPaymentAccountId, out var errors);
        if (input.HolderUserId is { } holder && !await cards.IsHolderOfWorkspaceAsync(workspaceId, holder, cancellationToken)) errors["holderUserId"] = ["O titular não pertence à família."];
        if (input.DefaultPaymentAccountId is { } account && !await cards.IsActiveAccountOfWorkspaceAsync(workspaceId, account, cancellationToken)) errors["defaultPaymentAccountId"] = ["A conta de pagamento precisa ser uma conta bancária ativa do orçamento."];
        return (errors.Count == 0 ? details : null, errors);
    }

    private async Task<bool> IsParticipantAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken) =>
        await workspaces.GetOwnerRoleAsync(workspaceId, userId, cancellationToken) is not null;
}
