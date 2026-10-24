using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Categories;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.CreditCards;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.CreditCards;

internal sealed class StatementCategoryContextRepository(Saldo360DbContext db) : IStatementCategoryContextRepository
{
    public async Task<StatementCategoryCatalog> ReadAsync(Guid workspaceId, Guid cardId, Guid excludedDocumentId, CancellationToken ct)
    {
        var categories = await db.FinancialCategories.AsNoTracking().Where(c => c.WorkspaceId == workspaceId)
            .OrderBy(c => c.Id).Select(c => new FinancialCategoryDto(c.Id, c.Name, c.Type, c.IsActive, c.CreatedAt)).ToArrayAsync(ct);
        var purchases = await db.FinancialCreditCardPurchases.AsNoTracking().Where(p => p.WorkspaceId == workspaceId && p.CreditCardId == cardId && p.Status == CreditCardPurchaseStatus.Active && p.Installments.Any(i => i.Status == CreditCardInstallmentStatus.Active))
            .OrderByDescending(p => p.UpdatedAt).ThenBy(p => p.Id)
            .Select(p => new { p.Description, p.PurchaseDate, p.TotalAmount, p.CategoryId }).ToArrayAsync(ct);
        var history = purchases.Where(p => categories.Any(c => c.Id == p.CategoryId && c.Type == Saldo360.Domain.Categories.CategoryType.Expense))
            .Select(p => new StatementCategoryHistory(p.Description, p.PurchaseDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                Saldo360.Domain.Common.Money.Format(p.TotalAmount), categories.Single(c => c.Id == p.CategoryId).Name)).ToList();
        var officialChoices = await (from i in db.FinancialCreditCardInstallments.AsNoTracking()
            join p in db.FinancialCreditCardPurchases.AsNoTracking() on new { i.WorkspaceId, Id = i.PurchaseId } equals new { p.WorkspaceId, p.Id }
            where i.WorkspaceId == workspaceId && p.CreditCardId == cardId && p.Status == CreditCardPurchaseStatus.Active && i.Status == CreditCardInstallmentStatus.Active && i.OfficialCategoryId != null
            orderby i.Id select new { p.Description, p.PurchaseDate, i.Amount, CategoryId = i.OfficialCategoryId!.Value }).ToArrayAsync(ct);
        history.AddRange(officialChoices.Where(p => categories.Any(c => c.Id == p.CategoryId && c.Type == Saldo360.Domain.Categories.CategoryType.Expense))
            .Select(p => new StatementCategoryHistory(p.Description, p.PurchaseDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                Saldo360.Domain.Common.Money.Format(p.Amount), categories.Single(c => c.Id == p.CategoryId).Name)));
        var reviewedDocuments = await db.FinancialDocumentInboxItems.AsNoTracking()
            .Where(d => d.WorkspaceId == workspaceId && d.Id != excludedDocumentId && d.StatementAnalysisJson != null)
            .OrderByDescending(d => d.UpdatedAt).ThenBy(d => d.Id).Select(d => d.StatementAnalysisJson).ToArrayAsync(ct);
        foreach (var json in reviewedDocuments)
        {
            var analysis = InboxStatementAnalysisService.Read(json);
            if (analysis?.CardId != cardId) continue;
            foreach (var choice in analysis.ManualDecisions.Where(c => analysis.ManualOverrides.TryGetValue(c.Key, out var confirmed) && confirmed == c.CategoryId))
            {
                var category = categories.SingleOrDefault(c => c.Id == choice.CategoryId && c.Type == Saldo360.Domain.Categories.CategoryType.Expense);
                if (category is not null && choice.TransactionDate is not null)
                    history.Add(new(choice.Description, choice.TransactionDate, choice.Amount, category.Name, "ManualReview"));
            }
        }
        return new(categories, history);
    }
}
