using Saldo360.Application.Accounts;
using Saldo360.Application.Families;
using Saldo360.Domain.CreditCards;

namespace Saldo360.Application.CreditCards;

public sealed class FinancialCreditCardService(
    IFamilyRepository families,
    IFinancialCreditCardRepository cards,
    IFinancialPlannedInvoiceReconciliationRepository reconciliations,
    TimeProvider timeProvider)
{
    public async Task<CreditCardResult> CreateAsync(Guid familyId, Guid userId, CreditCardInput input, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(familyId, userId, cancellationToken)) return CreditCardResult.NotFound;
        var (details, errors) = await ValidateAsync(familyId, input, cancellationToken);
        if (details is null) return CreditCardResult.Invalid(errors);
        var card = FinancialCreditCard.Create(familyId, details, userId, timeProvider.GetUtcNow());
        await cards.AddAsync(card, cancellationToken);
        return CreditCardResult.Ok((await cards.GetAsync(familyId, card.Id, cancellationToken))!);
    }

    public async Task<IReadOnlyList<FinancialCreditCardDto>?> ListAsync(Guid familyId, Guid userId, bool includeArchived, CancellationToken cancellationToken) =>
        await IsParticipantAsync(familyId, userId, cancellationToken) ? await cards.ListAsync(familyId, includeArchived, cancellationToken) : null;

    public async Task<FinancialCreditCardDto?> GetAsync(Guid familyId, Guid cardId, Guid userId, CancellationToken cancellationToken) =>
        await IsParticipantAsync(familyId, userId, cancellationToken) ? await cards.GetAsync(familyId, cardId, cancellationToken) : null;

    public async Task<CreditCardResult> UpdateAsync(Guid familyId, Guid cardId, Guid userId, CreditCardInput input, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(familyId, userId, cancellationToken) || await cards.FindAsync(familyId, cardId, cancellationToken) is not { } card)
            return CreditCardResult.NotFound;
        if (card.IsArchived) return CreditCardResult.Invalid(new Dictionary<string, string[]> { ["general"] = ["Cartão arquivado não pode ser alterado."] });
        var (details, errors) = await ValidateAsync(familyId, input, cancellationToken);
        if (details is null) return CreditCardResult.Invalid(errors);
        if (card.DefaultPaymentAccountId != details.DefaultPaymentAccountId
            && await reconciliations.HasActiveForCardAsync(familyId, cardId, cancellationToken))
        {
            return CreditCardResult.Invalid(new Dictionary<string, string[]> { ["defaultPaymentAccountId"] = ["A conta preferencial não pode ser alterada enquanto houver conciliações ativas; desfaça os vínculos primeiro."] });
        }
        card.Update(details, userId, timeProvider.GetUtcNow());
        await cards.SaveChangesAsync(cancellationToken);
        return CreditCardResult.Ok((await cards.GetAsync(familyId, card.Id, cancellationToken))!);
    }

    public async Task<bool> ArchiveAsync(Guid familyId, Guid cardId, Guid userId, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(familyId, userId, cancellationToken) || await cards.FindAsync(familyId, cardId, cancellationToken) is not { } card) return false;
        card.Archive(userId, timeProvider.GetUtcNow());
        await cards.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<(CreditCardDetails? Details, Dictionary<string, string[]> Errors)> ValidateAsync(Guid familyId, CreditCardInput input, CancellationToken cancellationToken)
    {
        var details = CreditCardDetails.TryCreate(input.Name, input.Issuer, input.HolderUserId, input.CreditLimit, input.ClosingDay, input.DueDay, input.DefaultPaymentAccountId, out var errors);
        if (input.HolderUserId is { } holder && !await cards.IsHolderOfFamilyAsync(familyId, holder, cancellationToken)) errors["holderUserId"] = ["O titular não pertence à família."];
        if (input.DefaultPaymentAccountId is { } account && !await cards.IsActiveAccountOfFamilyAsync(familyId, account, cancellationToken)) errors["defaultPaymentAccountId"] = ["A conta de pagamento não pertence à família ou está inativa."];
        return (errors.Count == 0 ? details : null, errors);
    }

    private async Task<bool> IsParticipantAsync(Guid familyId, Guid userId, CancellationToken cancellationToken) =>
        await families.GetMemberRoleAsync(familyId, userId, cancellationToken) is not null;
}
