using Saldo360.Application.Workspaces;
using Saldo360.Domain.CreditCards;

namespace Saldo360.Application.CreditCards;

public sealed class FinancialCreditCardPurchaseService(IFinancialWorkspaceRepository workspaces, IFinancialCreditCardPurchaseRepository purchases, TimeProvider timeProvider)
{
    public async Task<CreditCardPurchaseResult> CreateAsync(Guid workspaceId, Guid cardId, Guid userId, CreditCardPurchaseInput input, CancellationToken cancellationToken)
    {
        if (!await IsParticipant(workspaceId, userId, cancellationToken) || !await purchases.IsActiveCardAsync(workspaceId, cardId, cancellationToken)) return CreditCardPurchaseResult.NotFound;
        var errors = new Dictionary<string, string[]>();
        var validation = CreditCardPurchaseDetails.Validate(input.Description, input.TotalAmount, input.InstallmentCount, out var description);
        if (validation is not null) errors[validation.Contains("descrição") ? "description" : validation.Contains("valor") ? "totalAmount" : "installmentCount"] = [validation];
        if (input.PurchaseDate is null) errors["purchaseDate"] = ["Informe a data da compra."];
        if (input.CategoryId is null || !await purchases.IsExpenseCategoryAsync(workspaceId, input.CategoryId.Value, cancellationToken)) errors["categoryId"] = ["A categoria deve ser uma despesa da família."];
        if (errors.Count > 0) return CreditCardPurchaseResult.Invalid(errors);
        var calendar = await purchases.GetCardCalendarAsync(workspaceId, cardId, cancellationToken);
        if (calendar is null) return CreditCardPurchaseResult.NotFound;
        var purchase = FinancialCreditCardPurchase.Create(workspaceId, cardId, input.CategoryId!.Value, description!, input.PurchaseDate!.Value, input.TotalAmount!.Value, input.InstallmentCount!.Value, userId, timeProvider.GetUtcNow());
        await purchases.AddAsync(purchase, cancellationToken);
        foreach (var installment in purchase.Installments)
        {
            var cycle = CreditCardInvoiceCalendarCalculator.ForInstallment(purchase.PurchaseDate, installment.InstallmentNumber, calendar.ClosingDay, calendar.DueDay);
            var invoice = await purchases.FindInvoiceAsync(workspaceId, cardId, cycle.ClosingDate, cancellationToken);
            if (invoice is null)
            {
                invoice = FinancialCreditCardInvoice.Create(workspaceId, cardId, cycle.Competence, cycle.CycleStartDate, cycle.ClosingDate, cycle.DueDate, timeProvider.GetUtcNow());
                await purchases.AddInvoiceAsync(invoice, cancellationToken);
            }
            installment.AssignInvoice(invoice.Id);
        }
        await purchases.SaveChangesAsync(cancellationToken);
        return CreditCardPurchaseResult.Ok((await purchases.GetAsync(workspaceId, cardId, purchase.Id, cancellationToken))!);
    }

    public async Task<IReadOnlyList<FinancialCreditCardPurchaseDto>?> ListAsync(Guid workspaceId, Guid cardId, Guid userId, DateOnly? from, DateOnly? through, CancellationToken cancellationToken) =>
        await IsParticipant(workspaceId, userId, cancellationToken) ? await purchases.ListAsync(workspaceId, cardId, from, through, cancellationToken) : null;

    public async Task<FinancialCreditCardPurchaseDto?> GetAsync(Guid workspaceId, Guid cardId, Guid purchaseId, Guid userId, CancellationToken cancellationToken) =>
        await IsParticipant(workspaceId, userId, cancellationToken) ? await purchases.GetAsync(workspaceId, cardId, purchaseId, cancellationToken) : null;

    public async Task<bool> CancelAsync(Guid workspaceId, Guid cardId, Guid purchaseId, Guid userId, CancellationToken cancellationToken)
    {
        if (!await IsParticipant(workspaceId, userId, cancellationToken) || await purchases.FindAsync(workspaceId, cardId, purchaseId, cancellationToken) is not { } purchase) return false;
        if (await purchases.HasActiveReconciliationAsync(workspaceId, cardId, purchaseId, cancellationToken)) return false;
        if (!await purchases.CanCancelAsync(workspaceId, cardId, purchaseId, cancellationToken)) return false;
        purchase.Cancel(userId, timeProvider.GetUtcNow());
        await purchases.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<FinancialCreditCardInvoiceDto>?> ListInvoicesAsync(Guid workspaceId, Guid cardId, Guid userId, string from, string through, DateOnly today, CancellationToken cancellationToken) =>
        await IsParticipant(workspaceId, userId, cancellationToken) && await purchases.GetCardCalendarAsync(workspaceId, cardId, cancellationToken) is not null
            ? await purchases.ListInvoicesAsync(workspaceId, cardId, from, through, today, cancellationToken) : null;

    public async Task<FinancialCreditCardInvoiceDto?> GetInvoiceAsync(Guid workspaceId, Guid cardId, Guid invoiceId, Guid userId, DateOnly today, CancellationToken cancellationToken) =>
        await IsParticipant(workspaceId, userId, cancellationToken) ? await purchases.GetInvoiceAsync(workspaceId, cardId, invoiceId, today, cancellationToken) : null;

    public async Task<IReadOnlyList<FinancialCreditCardInvoiceDto>?> ProjectInvoicesAsync(Guid workspaceId, Guid cardId, Guid userId, string from, int months, DateOnly today, CancellationToken cancellationToken) =>
        await IsParticipant(workspaceId, userId, cancellationToken) ? await purchases.ProjectInvoicesAsync(workspaceId, cardId, from, months, today, cancellationToken) : null;

    public async Task<FinancialCreditCardLimitDto?> GetLimitAsync(Guid workspaceId, Guid cardId, Guid userId, CancellationToken cancellationToken) =>
        await IsParticipant(workspaceId, userId, cancellationToken) ? await purchases.GetLimitAsync(workspaceId, cardId, cancellationToken) : null;

    private async Task<bool> IsParticipant(Guid workspaceId, Guid userId, CancellationToken cancellationToken) => await workspaces.GetOwnerRoleAsync(workspaceId, userId, cancellationToken) is not null;
}
