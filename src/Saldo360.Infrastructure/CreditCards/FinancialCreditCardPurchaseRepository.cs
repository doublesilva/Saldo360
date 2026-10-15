using Microsoft.EntityFrameworkCore;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;
using Saldo360.Domain.CreditCards;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.CreditCards;

internal sealed class FinancialCreditCardPurchaseRepository(Saldo360DbContext db) : IFinancialCreditCardPurchaseRepository
{
    public async Task AddAsync(FinancialCreditCardPurchase purchase, CancellationToken cancellationToken) { db.FinancialCreditCardPurchases.Add(purchase); await db.SaveChangesAsync(cancellationToken); }
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
    public Task<bool> IsActiveCardAsync(Guid familyId, Guid cardId, CancellationToken cancellationToken) => db.FinancialCreditCards.AnyAsync(c => c.FamilyId == familyId && c.Id == cardId && !c.IsArchived, cancellationToken);
    public Task<bool> IsExpenseCategoryAsync(Guid familyId, Guid categoryId, CancellationToken cancellationToken) => db.FinancialCategories.AnyAsync(c => c.FamilyId == familyId && c.Id == categoryId && c.Type == CategoryType.Expense, cancellationToken);

    private static FinancialCreditCardPurchaseDto? ToDto(FinancialCreditCardPurchase? purchase) => purchase is null ? null : new(
        purchase.Id, purchase.CreditCardId, purchase.CategoryId, purchase.Description, purchase.PurchaseDate, Money.Format(purchase.TotalAmount), purchase.InstallmentCount, purchase.Status,
        purchase.Installments.OrderBy(i => i.InstallmentNumber).Select(i => new FinancialCreditCardInstallmentDto(i.Id, i.InstallmentNumber, Money.Format(i.Amount), i.Status)).ToList());
}
