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
        var calendar = await purchases.GetCardCalendarAsync(familyId, cardId, cancellationToken);
        if (calendar is null) return CreditCardPurchaseResult.NotFound;
        var purchase = FinancialCreditCardPurchase.Create(familyId, cardId, input.CategoryId!.Value, description!, input.PurchaseDate!.Value, input.TotalAmount!.Value, input.InstallmentCount!.Value, userId, timeProvider.GetUtcNow());
        await purchases.AddAsync(purchase, cancellationToken);
        foreach (var installment in purchase.Installments)
        {
            var cycle = CreditCardInvoiceCalendarCalculator.ForInstallment(purchase.PurchaseDate, installment.InstallmentNumber, calendar.ClosingDay, calendar.DueDay);
            var invoice = await purchases.FindInvoiceAsync(familyId, cardId, cycle.ClosingDate, cancellationToken);
            if (invoice is null)
            {
                invoice = FinancialCreditCardInvoice.Create(familyId, cardId, cycle.Competence, cycle.CycleStartDate, cycle.ClosingDate, cycle.DueDate, timeProvider.GetUtcNow());
                await purchases.AddInvoiceAsync(invoice, cancellationToken);
            }
            installment.AssignInvoice(invoice.Id);
        }
        await purchases.SaveChangesAsync(cancellationToken);
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

    public async Task<IReadOnlyList<FinancialCreditCardInvoiceDto>?> ListInvoicesAsync(Guid familyId, Guid cardId, Guid userId, string from, string through, DateOnly today, CancellationToken cancellationToken) =>
        await IsParticipant(familyId, userId, cancellationToken) && await purchases.GetCardCalendarAsync(familyId, cardId, cancellationToken) is not null
            ? await purchases.ListInvoicesAsync(familyId, cardId, from, through, today, cancellationToken) : null;

    public async Task<FinancialCreditCardInvoiceDto?> GetInvoiceAsync(Guid familyId, Guid cardId, Guid invoiceId, Guid userId, DateOnly today, CancellationToken cancellationToken) =>
        await IsParticipant(familyId, userId, cancellationToken) ? await purchases.GetInvoiceAsync(familyId, cardId, invoiceId, today, cancellationToken) : null;

    private async Task<bool> IsParticipant(Guid familyId, Guid userId, CancellationToken cancellationToken) => await families.GetMemberRoleAsync(familyId, userId, cancellationToken) is not null;
}
