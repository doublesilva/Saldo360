using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.CreditCards;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.CreditCards;

internal sealed class FinancialCreditCardImportRepository(Saldo360DbContext db) : IFinancialCreditCardImportRepository
{
    public Task<FinancialCreditCardImportBatch?> FindBatchAsync(Guid importId, CancellationToken cancellationToken) =>
        db.FinancialCreditCardImportBatches.Include(item => item.Items).SingleOrDefaultAsync(item => item.Id == importId, cancellationToken);

    public Task<bool> IsExpenseCategoryAsync(Guid workspaceId, Guid categoryId, CancellationToken cancellationToken) =>
        db.FinancialCategories.AnyAsync(item => item.WorkspaceId == workspaceId && item.Id == categoryId && item.Type == Domain.Categories.CategoryType.Expense && item.IsActive, cancellationToken);

    public async Task<CreditCardImportRepositoryResult> ConfirmAsync(Guid workspaceId, Guid cardId, Guid userId, Guid importId, string payloadHash, IReadOnlyList<CreditCardImportItemInput> items, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var existing = await db.FinancialCreditCardImportBatches.Include(item => item.Items).SingleOrDefaultAsync(item => item.Id == importId, cancellationToken);
        if (existing is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            if (existing.WorkspaceId != workspaceId || existing.CreditCardId != cardId) return new(null, true, false, "O identificador da operação já pertence a outro contexto.");
            if (!string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal)) return new(null, true, false, "O mesmo importId foi enviado com conteúdo diferente.");
            return new(ToResult(existing), false, false);
        }

        var card = await db.FinancialCreditCards.SingleOrDefaultAsync(item => item.WorkspaceId == workspaceId && item.Id == cardId && !item.IsArchived, cancellationToken);
        var calendar = card is null ? null : await db.FinancialCreditCards.Where(item => item.WorkspaceId == workspaceId && item.Id == cardId).Select(item => new CreditCardCalendarConfig(item.ClosingDay, item.DueDay)).SingleOrDefaultAsync(cancellationToken);
        if (card is null || calendar is null) { await transaction.RollbackAsync(cancellationToken); return new(null, false, true); }

        var batch = FinancialCreditCardImportBatch.Create(importId, workspaceId, cardId, payloadHash, userId, now);
        db.FinancialCreditCardImportBatches.Add(batch);
        foreach (var item in items)
        {
            var date = DateOnly.ParseExact(item.TransactionDate, "yyyy-MM-dd");
            var total = decimal.Parse(item.TotalAmount ?? item.Amount, System.Globalization.CultureInfo.InvariantCulture);
            var count = item.InstallmentCount ?? 1;
            var purchase = FinancialCreditCardPurchase.Create(workspaceId, cardId, item.CategoryId, item.Description, date, total, count, userId, now);
            await AddAsync(purchase, cancellationToken);
            foreach (var installment in purchase.Installments)
            {
                var cycle = CreditCardInvoiceCalendarCalculator.ForInstallment(purchase.PurchaseDate, installment.InstallmentNumber, calendar.ClosingDay, calendar.DueDay);
                var invoice = await db.FinancialCreditCardInvoices.SingleOrDefaultAsync(value => value.WorkspaceId == workspaceId && value.CreditCardId == cardId && value.ClosingDate == cycle.ClosingDate, cancellationToken);
                if (invoice is null)
                {
                    invoice = FinancialCreditCardInvoice.Create(workspaceId, cardId, cycle.Competence, cycle.CycleStartDate, cycle.ClosingDate, cycle.DueDate, now);
                    db.FinancialCreditCardInvoices.Add(invoice);
                }
                installment.AssignInvoice(invoice.Id);
            }
            batch.AddItem(FinancialCreditCardImportItem.Create(workspaceId, cardId, importId, item.RowNumber, item.ExternalId, purchase.Id, now));
        }
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(ToResult(batch), false, false);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            var raced = await db.FinancialCreditCardImportBatches.Include(item => item.Items).SingleOrDefaultAsync(item => item.Id == importId, cancellationToken);
            if (raced is not null && raced.WorkspaceId == workspaceId && raced.CreditCardId == cardId && raced.PayloadHash == payloadHash) return new(ToResult(raced), false, false);
            return new(null, true, false, "A operação ou identificador externo já foi importado.");
        }
    }

    private Task AddAsync(FinancialCreditCardPurchase purchase, CancellationToken cancellationToken) { db.FinancialCreditCardPurchases.Add(purchase); return Task.CompletedTask; }

    private static CreditCardImportResult ToResult(FinancialCreditCardImportBatch batch) =>
        new(batch.Id, batch.Status.ToString(), batch.Items.Count, batch.Items.OrderBy(item => item.RowNumber).Select(item => new CreditCardImportItemResult(item.RowNumber, item.Status.ToString(), item.PurchaseId, [])).ToList());
}
