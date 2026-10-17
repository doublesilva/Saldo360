using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.CreditCards;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.CreditCards;

internal sealed class FinancialPlannedInvoiceReconciliationRepository(Saldo360DbContext db)
    : IFinancialPlannedInvoiceReconciliationRepository
{
    public async Task<IReadOnlyList<FinancialPlannedInvoiceReconciliationDto>?> ListAsync(Guid familyId, Guid? invoiceId, Guid? plannedTransactionId, CancellationToken cancellationToken)
    {
        var query = db.FinancialPlannedInvoiceReconciliations.AsNoTracking()
            .Where(item => item.FamilyId == familyId);
        if (invoiceId is { } invoice) query = query.Where(item => item.CreditCardInvoiceId == invoice);
        if (plannedTransactionId is { } planned) query = query.Where(item => item.PlannedTransactionId == planned);
        return await query.OrderByDescending(item => item.CreatedAt)
            .Select(item => ToDto(item)).ToListAsync(cancellationToken);
    }

    public Task<FinancialPlannedInvoiceReconciliationDto?> GetAsync(Guid familyId, Guid id, CancellationToken cancellationToken) =>
        db.FinancialPlannedInvoiceReconciliations.AsNoTracking()
            .Where(item => item.FamilyId == familyId && item.Id == id)
            .Select(item => ToDto(item)).SingleOrDefaultAsync(cancellationToken);

    public Task<FinancialPlannedInvoiceReconciliationDto?> FindActiveAsync(Guid familyId, Guid plannedTransactionId, Guid invoiceId, CancellationToken cancellationToken) =>
        db.FinancialPlannedInvoiceReconciliations.AsNoTracking()
            .Where(item => item.FamilyId == familyId && item.PlannedTransactionId == plannedTransactionId
                && item.CreditCardInvoiceId == invoiceId && item.RemovedAt == null)
            .Select(item => ToDto(item)).SingleOrDefaultAsync(cancellationToken);

    public async Task<ReconciliationEligibility?> GetEligibilityAsync(Guid familyId, Guid plannedTransactionId, Guid invoiceId, CancellationToken cancellationToken)
    {
        var planned = await db.FinancialPlannedTransactions.AsNoTracking()
            .Where(item => item.FamilyId == familyId && item.Id == plannedTransactionId)
            .Select(item => new { item.AccountId, item.Type, item.IsCancelled, item.Amount })
            .SingleOrDefaultAsync(cancellationToken);
        var invoice = await (from item in db.FinancialCreditCardInvoices.AsNoTracking()
                             join card in db.FinancialCreditCards.AsNoTracking()
                                 on new { item.FamilyId, CardId = item.CreditCardId } equals new { card.FamilyId, CardId = card.Id }
                             where item.FamilyId == familyId && item.Id == invoiceId
                             select new { item.Id, card.DefaultPaymentAccountId }).SingleOrDefaultAsync(cancellationToken);
        if (planned is null && invoice is null) return null;

        var paid = invoice is null ? 0m : await db.FinancialCreditCardInvoicePayments.AsNoTracking()
            .Where(item => item.FamilyId == familyId && item.InvoiceId == invoiceId && item.Status == FinancialCreditCardInvoicePaymentStatus.Active)
            .SumAsync(item => (decimal?)item.Amount, cancellationToken) ?? 0m;
        var total = invoice is null ? 0m : await (from installment in db.FinancialCreditCardInstallments.AsNoTracking()
                                                   join purchase in db.FinancialCreditCardPurchases.AsNoTracking() on installment.PurchaseId equals purchase.Id
                                                   where installment.FamilyId == familyId && installment.InvoiceId == invoiceId
                                                       && installment.Status == CreditCardInstallmentStatus.Active
                                                       && purchase.Status == CreditCardPurchaseStatus.Active
                                                   select installment.Amount).SumAsync(cancellationToken);
        if (invoice is not null)
        {
            total += await db.FinancialCreditCardImportedInstallments.AsNoTracking().Where(item => item.FamilyId == familyId && item.InvoiceId == invoiceId && item.IsHistorical && item.Status == FinancialCreditCardImportedInstallmentStatus.Active).SumAsync(item => (decimal?)item.Amount, cancellationToken) ?? 0m;
            total += await db.FinancialCreditCardInvoiceAdjustments.AsNoTracking().Where(item => item.FamilyId == familyId && item.InvoiceId == invoiceId && item.Status == FinancialCreditCardInvoiceAdjustmentStatus.Active).SumAsync(item => (decimal?)(item.Nature == FinancialCreditCardInvoiceAdjustmentNature.Debit ? item.Amount : -item.Amount), cancellationToken) ?? 0m;
        }
        var plannedLinked = planned is null || await db.FinancialPlannedInvoiceReconciliations.AnyAsync(item => item.FamilyId == familyId && item.PlannedTransactionId == plannedTransactionId && item.RemovedAt == null, cancellationToken);
        var invoiceLinked = invoice is null || await db.FinancialPlannedInvoiceReconciliations.AnyAsync(item => item.FamilyId == familyId && item.CreditCardInvoiceId == invoiceId && item.RemovedAt == null, cancellationToken);
        var realized = planned is null || await db.FinancialTransactions.AnyAsync(item => item.FamilyId == familyId && item.PlannedTransactionId == plannedTransactionId && !item.IsCancelled, cancellationToken);
        return new(
            planned is not null,
            invoice is not null,
            planned is not null && planned.Type == Domain.Categories.CategoryType.Expense && !planned.IsCancelled && !realized,
            invoice is not null && total > paid,
            planned is not null && invoice?.DefaultPaymentAccountId is { } accountId && planned.AccountId == accountId,
            plannedLinked,
            invoiceLinked,
            planned?.AccountId ?? Guid.Empty,
            invoice?.DefaultPaymentAccountId);
    }

    public async Task<FinancialPlannedInvoiceReconciliationDto?> CreateAsync(Guid familyId, Guid plannedTransactionId, Guid invoiceId, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var entity = FinancialPlannedInvoiceReconciliation.Create(familyId, plannedTransactionId, invoiceId, userId, now);
        db.FinancialPlannedInvoiceReconciliations.Add(entity);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return await GetAsync(familyId, entity.Id, cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            return null;
        }
    }

    public async Task<bool> RemoveAsync(Guid familyId, Guid id, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var entity = await db.FinancialPlannedInvoiceReconciliations.SingleOrDefaultAsync(item => item.FamilyId == familyId && item.Id == id, cancellationToken);
        if (entity is null) return false;
        if (entity.RemovedAt is not null) return true;
        entity.Remove(userId, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public Task<bool> HasActiveForCardAsync(Guid familyId, Guid cardId, CancellationToken cancellationToken) =>
        (from reconciliation in db.FinancialPlannedInvoiceReconciliations
         join invoice in db.FinancialCreditCardInvoices on new { reconciliation.FamilyId, Id = reconciliation.CreditCardInvoiceId } equals new { invoice.FamilyId, Id = invoice.Id }
         where reconciliation.FamilyId == familyId && invoice.CreditCardId == cardId && reconciliation.RemovedAt == null
         select reconciliation.Id).AnyAsync(cancellationToken);

    private static FinancialPlannedInvoiceReconciliationDto ToDto(FinancialPlannedInvoiceReconciliation item) =>
        new(item.Id, item.FamilyId, item.PlannedTransactionId, item.CreditCardInvoiceId, item.CreatedAt, item.RemovedAt);
}
