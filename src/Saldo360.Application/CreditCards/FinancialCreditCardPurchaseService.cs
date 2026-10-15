using Saldo360.Application.Families;
using Saldo360.Domain.CreditCards;

namespace Saldo360.Application.CreditCards;

public sealed class FinancialCreditCardPurchaseService(IFamilyRepository families, IFinancialCreditCardPurchaseRepository purchases, TimeProvider timeProvider)
{
    public async Task<CreditCardPurchaseResult> CreateAsync(Guid familyId, Guid cardId, Guid userId, CreditCardPurchaseInput input, CancellationToken cancellationToken)
    {
        if (!await IsParticipant(familyId, userId, cancellationToken) || !await purchases.IsActiveCardAsync(familyId, cardId, cancellationToken)) return CreditCardPurchaseResult.NotFound;
        var errors = new Dictionary<string, string[]>();
        var validation = CreditCardPurchaseDetails.Validate(input.Description, input.TotalAmount, input.InstallmentCount, out var description);
        if (validation is not null) errors[validation.Contains("descrição") ? "description" : validation.Contains("valor") ? "totalAmount" : "installmentCount"] = [validation];
        if (input.PurchaseDate is null) errors["purchaseDate"] = ["Informe a data da compra."];
        if (input.CategoryId is null || !await purchases.IsExpenseCategoryAsync(familyId, input.CategoryId.Value, cancellationToken)) errors["categoryId"] = ["A categoria deve ser uma despesa da família."];
        if (errors.Count > 0) return CreditCardPurchaseResult.Invalid(errors);
        var purchase = FinancialCreditCardPurchase.Create(familyId, cardId, input.CategoryId!.Value, description!, input.PurchaseDate!.Value, input.TotalAmount!.Value, input.InstallmentCount!.Value, userId, timeProvider.GetUtcNow());
        await purchases.AddAsync(purchase, cancellationToken);
        return CreditCardPurchaseResult.Ok((await purchases.GetAsync(familyId, cardId, purchase.Id, cancellationToken))!);
    }

    public async Task<IReadOnlyList<FinancialCreditCardPurchaseDto>?> ListAsync(Guid familyId, Guid cardId, Guid userId, DateOnly? from, DateOnly? through, CancellationToken cancellationToken) =>
        await IsParticipant(familyId, userId, cancellationToken) ? await purchases.ListAsync(familyId, cardId, from, through, cancellationToken) : null;

    public async Task<FinancialCreditCardPurchaseDto?> GetAsync(Guid familyId, Guid cardId, Guid purchaseId, Guid userId, CancellationToken cancellationToken) =>
        await IsParticipant(familyId, userId, cancellationToken) ? await purchases.GetAsync(familyId, cardId, purchaseId, cancellationToken) : null;

    public async Task<bool> CancelAsync(Guid familyId, Guid cardId, Guid purchaseId, Guid userId, CancellationToken cancellationToken)
    {
        if (!await IsParticipant(familyId, userId, cancellationToken) || await purchases.FindAsync(familyId, cardId, purchaseId, cancellationToken) is not { } purchase) return false;
        purchase.Cancel(userId, timeProvider.GetUtcNow());
        await purchases.SaveChangesAsync(cancellationToken);
        return true;
    }

    private async Task<bool> IsParticipant(Guid familyId, Guid userId, CancellationToken cancellationToken) => await families.GetMemberRoleAsync(familyId, userId, cancellationToken) is not null;
}
