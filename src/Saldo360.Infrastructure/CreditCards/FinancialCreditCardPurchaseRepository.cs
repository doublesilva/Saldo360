using Microsoft.EntityFrameworkCore;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;
using Saldo360.Domain.CreditCards;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.CreditCards;

internal sealed class FinancialCreditCardPurchaseRepository(Saldo360DbContext db) : IFinancialCreditCardPurchaseRepository
{
    public Task AddAsync(FinancialCreditCardPurchase purchase, CancellationToken cancellationToken) { db.FinancialCreditCardPurchases.Add(purchase); return Task.CompletedTask; }
    public Task<FinancialCreditCardPurchase?> FindAsync(Guid familyId, Guid cardId, Guid purchaseId, CancellationToken cancellationToken) => db.FinancialCreditCardPurchases.Include(p => p.Installments).SingleOrDefaultAsync(p => p.FamilyId == familyId && p.CreditCardId == cardId && p.Id == purchaseId, cancellationToken);
    public async Task<FinancialCreditCardPurchaseDto?> GetAsync(Guid familyId, Guid cardId, Guid purchaseId, CancellationToken cancellationToken) => ToDto(await FindAsync(familyId, cardId, purchaseId, cancellationToken));
    public async Task<IReadOnlyList<FinancialCreditCardPurchaseDto>> ListAsync(Guid familyId, Guid cardId, DateOnly? from, DateOnly? through, CancellationToken cancellationToken)
    {
        var query = db.FinancialCreditCardPurchases.AsNoTracking().Include(p => p.Installments).Where(p => p.FamilyId == familyId && p.CreditCardId == cardId);
        if (from is { } start) query = query.Where(p => p.PurchaseDate >= start);
        if (through is { } end) query = query.Where(p => p.PurchaseDate <= end);
        return (await query.OrderBy(p => p.PurchaseDate).ThenBy(p => p.CreatedAt).ToListAsync(cancellationToken)).Select(p => ToDto(p)!).ToList();
    }
    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
    public Task<CreditCardCalendarConfig?> GetCardCalendarAsync(Guid familyId, Guid cardId, CancellationToken cancellationToken) => db.FinancialCreditCards.Where(c => c.FamilyId == familyId && c.Id == cardId).Select(c => new CreditCardCalendarConfig(c.ClosingDay, c.DueDay)).SingleOrDefaultAsync(cancellationToken);
    public Task<FinancialCreditCardInvoice?> FindInvoiceAsync(Guid familyId, Guid cardId, DateOnly closingDate, CancellationToken cancellationToken) => db.FinancialCreditCardInvoices.SingleOrDefaultAsync(i => i.FamilyId == familyId && i.CreditCardId == cardId && i.ClosingDate == closingDate, cancellationToken);
    public Task AddInvoiceAsync(FinancialCreditCardInvoice invoice, CancellationToken cancellationToken) { db.FinancialCreditCardInvoices.Add(invoice); return Task.CompletedTask; }

    public async Task<IReadOnlyList<FinancialCreditCardInvoiceDto>> ListInvoicesAsync(Guid familyId, Guid cardId, string from, string through, DateOnly today, CancellationToken cancellationToken)
    {
        var invoices = (await db.FinancialCreditCardInvoices.AsNoTracking().Where(i => i.FamilyId == familyId && i.CreditCardId == cardId).OrderBy(i => i.ClosingDate).ToListAsync(cancellationToken)).Where(i => string.CompareOrdinal(i.Competence, from) >= 0 && string.CompareOrdinal(i.Competence, through) <= 0).ToList();
        return await MapInvoicesAsync(invoices, today, cancellationToken);
    }

    public async Task<FinancialCreditCardInvoiceDto?> GetInvoiceAsync(Guid familyId, Guid cardId, Guid invoiceId, DateOnly today, CancellationToken cancellationToken)
    {
        var invoice = await db.FinancialCreditCardInvoices.AsNoTracking().SingleOrDefaultAsync(i => i.FamilyId == familyId && i.CreditCardId == cardId && i.Id == invoiceId, cancellationToken);
        return invoice is null ? null : (await MapInvoicesAsync([invoice], today, cancellationToken)).Single();
    }

    private async Task<List<FinancialCreditCardInvoiceDto>> MapInvoicesAsync(List<FinancialCreditCardInvoice> invoices, DateOnly today, CancellationToken cancellationToken)
    {
        var ids = invoices.Select(i => i.Id).ToList();
        var rows = await (from installment in db.FinancialCreditCardInstallments.AsNoTracking()
                          join purchase in db.FinancialCreditCardPurchases.AsNoTracking() on installment.PurchaseId equals purchase.Id
                          where installment.InvoiceId != null && ids.Contains(installment.InvoiceId.Value) && installment.FamilyId == purchase.FamilyId && installment.Status == CreditCardInstallmentStatus.Active && purchase.Status == CreditCardPurchaseStatus.Active
                          select new { installment.InvoiceId, installment.Id, installment.PurchaseId, purchase.Description, purchase.InstallmentCount, installment.InstallmentNumber, installment.Amount, installment.Status }).ToListAsync(cancellationToken);
        return invoices.Select(invoice => new FinancialCreditCardInvoiceDto(invoice.Id, invoice.CreditCardId, invoice.Competence, invoice.CycleStartDate, invoice.ClosingDate, invoice.DueDate, invoice.GetStatus(today), Money.Format(rows.Where(r => r.InvoiceId == invoice.Id).Sum(r => r.Amount)), rows.Where(r => r.InvoiceId == invoice.Id).OrderBy(r => r.InstallmentNumber).Select(r => new FinancialCreditCardInvoiceInstallmentDto(r.Id, r.PurchaseId, r.Description, r.InstallmentNumber, r.InstallmentCount, Money.Format(r.Amount))).ToList())).ToList();
    }
    public Task<bool> IsActiveCardAsync(Guid familyId, Guid cardId, CancellationToken cancellationToken) => db.FinancialCreditCards.AnyAsync(c => c.FamilyId == familyId && c.Id == cardId && !c.IsArchived, cancellationToken);
    public Task<bool> IsExpenseCategoryAsync(Guid familyId, Guid categoryId, CancellationToken cancellationToken) => db.FinancialCategories.AnyAsync(c => c.FamilyId == familyId && c.Id == categoryId && c.Type == CategoryType.Expense, cancellationToken);

    private static FinancialCreditCardPurchaseDto? ToDto(FinancialCreditCardPurchase? purchase) => purchase is null ? null : new(
        purchase.Id, purchase.CreditCardId, purchase.CategoryId, purchase.Description, purchase.PurchaseDate, Money.Format(purchase.TotalAmount), purchase.InstallmentCount, purchase.Status,
        purchase.Installments.OrderBy(i => i.InstallmentNumber).Select(i => new FinancialCreditCardInstallmentDto(i.Id, i.InstallmentNumber, Money.Format(i.Amount), i.Status)).ToList());
}
