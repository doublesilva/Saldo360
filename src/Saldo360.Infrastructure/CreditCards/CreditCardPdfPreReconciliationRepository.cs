using System.Data;
using Microsoft.EntityFrameworkCore;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.CreditCards;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.CreditCards;

internal sealed class CreditCardPdfPreReconciliationRepository(Saldo360DbContext db)
    : ICreditCardPdfPreReconciliationRepository
{
    public async Task<CreditCardPdfPreReconciliationSnapshot?> GetSnapshotAsync(
        Guid workspaceId, Guid cardId, Guid userId, string? competence, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var card = await (from candidate in db.FinancialCreditCards.AsNoTracking()
                          where candidate.WorkspaceId == workspaceId && candidate.Id == cardId && !candidate.IsArchived
                              && db.FinancialWorkspaces.Any(member => member.Id == workspaceId && member.OwnerUserId == userId && member.IsActive && member.Type == Saldo360.Domain.Workspaces.FinancialWorkspaceType.Personal)
                          select new { candidate.Name, candidate.Issuer, candidate.LastFourDigits, candidate.HolderUserId })
            .SingleOrDefaultAsync(cancellationToken);
        if (card is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var invoice = competence is null ? null : await db.FinancialCreditCardInvoices.AsNoTracking()
            .Where(item => item.WorkspaceId == workspaceId && item.CreditCardId == cardId && item.Competence == competence)
            .Select(item => new CreditCardPdfInvoiceSnapshot(item.Id, item.Competence, item.ClosingDate, item.DueDate))
            .SingleOrDefaultAsync(cancellationToken);

        var purchaseOperations = await (
            from installment in db.FinancialCreditCardInstallments.AsNoTracking()
            join purchase in db.FinancialCreditCardPurchases.AsNoTracking()
                on new { installment.WorkspaceId, installment.PurchaseId }
                equals new { purchase.WorkspaceId, PurchaseId = purchase.Id }
            join invoiceRow in db.FinancialCreditCardInvoices.AsNoTracking()
                on new { installment.WorkspaceId, InvoiceId = installment.InvoiceId!.Value }
                equals new { invoiceRow.WorkspaceId, InvoiceId = invoiceRow.Id }
            join importItem in db.FinancialCreditCardImportItems.AsNoTracking()
                on new { purchase.WorkspaceId, PurchaseId = (Guid?)purchase.Id }
                equals new { importItem.WorkspaceId, importItem.PurchaseId } into imports
            from importItem in imports.DefaultIfEmpty()
            where purchase.WorkspaceId == workspaceId && purchase.CreditCardId == cardId
            select new CreditCardPdfExistingOperation(
                "Installment", installment.Id, importItem == null ? null : importItem.ExternalId,
                purchase.PurchaseDate, purchase.Description, installment.Amount,
                installment.InstallmentNumber, purchase.InstallmentCount, invoiceRow.Competence))
            .ToListAsync(cancellationToken);

        var historicalOperations = await db.FinancialCreditCardImportedInstallments.AsNoTracking()
            .Where(item => item.WorkspaceId == workspaceId && item.CreditCardId == cardId)
            .Join(db.FinancialCreditCardInvoices.AsNoTracking(), item => new { item.WorkspaceId, Id = item.InvoiceId }, invoiceRow => new { invoiceRow.WorkspaceId, invoiceRow.Id },
                (item, invoiceRow) => new CreditCardPdfExistingOperation(
                    item.IsHistorical ? "HistoricalInstallment" : "ImportedInstallmentLink",
                    item.Id, item.ExternalId, item.TransactionDate, item.Description, item.Amount,
                    item.InstallmentNumber, item.InstallmentCount, invoiceRow.Competence))
            .ToListAsync(cancellationToken);

        var paymentOperations = await (
            from payment in db.FinancialCreditCardInvoicePayments.AsNoTracking()
            join invoiceRow in db.FinancialCreditCardInvoices.AsNoTracking()
                on new { payment.WorkspaceId, InvoiceId = payment.InvoiceId }
                equals new { invoiceRow.WorkspaceId, InvoiceId = invoiceRow.Id }
            where payment.WorkspaceId == workspaceId && invoiceRow.CreditCardId == cardId
            select new CreditCardPdfExistingOperation(
                "InvoicePayment", payment.Id, null, payment.PaymentDate, "Pagamento de fatura",
                -payment.Amount, null, null, invoiceRow.Competence))
            .ToListAsync(cancellationToken);

        var adjustmentOperations = await db.FinancialCreditCardInvoiceAdjustments.AsNoTracking()
            .Where(item => item.WorkspaceId == workspaceId && item.CreditCardId == cardId)
            .Join(db.FinancialCreditCardInvoices.AsNoTracking(), item => new { item.WorkspaceId, Id = item.InvoiceId }, invoiceRow => new { invoiceRow.WorkspaceId, invoiceRow.Id },
                (item, invoiceRow) => new CreditCardPdfExistingOperation(
                    "InvoiceAdjustment", item.Id, item.ExternalId, item.TransactionDate, item.Description,
                    item.Nature == FinancialCreditCardInvoiceAdjustmentNature.Debit ? item.Amount : -item.Amount,
                    null, null, invoiceRow.Competence))
            .ToListAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new(card.Name, card.Issuer, card.LastFourDigits, card.HolderUserId, invoice,
            purchaseOperations.Concat(historicalOperations).Concat(paymentOperations).Concat(adjustmentOperations).ToList());
    }
}
