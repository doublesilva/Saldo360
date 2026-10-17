using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.Categories;
using Saldo360.Domain.CreditCards;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.CreditCards;

internal sealed class CreditCardPdfConfirmationRepository(Saldo360DbContext db) : ICreditCardPdfConfirmationRepository
{
    public async Task<CreditCardPdfConfirmationOperationResult> ConfirmAsync(
        Guid familyId, Guid cardId, Guid userId, CreditCardPdfTrustedConfirmation confirmation,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var existing = await db.FinancialCreditCardImportBatches.Include(batch => batch.Items)
            .SingleOrDefaultAsync(batch => batch.FamilyId == familyId && batch.CreditCardId == cardId
                && batch.Source == FinancialCreditCardImportSource.Pdf && batch.DocumentHash == confirmation.DocumentHash, cancellationToken);
        if (existing is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            if (existing.DecisionHash != confirmation.DecisionHash)
                return new(CreditCardPdfConfirmationAccess.Conflict, null, null, "O mesmo documento já foi confirmado com decisões diferentes.");
            return new(CreditCardPdfConfirmationAccess.Allowed, await ResultAsync(existing, confirmation.InvoiceId, cancellationToken));
        }

        var card = await db.FinancialCreditCards.SingleOrDefaultAsync(item => item.FamilyId == familyId && item.Id == cardId && !item.IsArchived, cancellationToken);
        var invoice = await db.FinancialCreditCardInvoices.SingleOrDefaultAsync(item => item.FamilyId == familyId && item.CreditCardId == cardId && item.Id == confirmation.InvoiceId, cancellationToken);
        if (card is null || invoice is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(CreditCardPdfConfirmationAccess.NotFound);
        }
        var batch = FinancialCreditCardImportBatch.CreatePdf(Guid.NewGuid(), familyId, cardId, confirmation.DocumentHash,
            confirmation.DecisionHash, confirmation.Parser, confirmation.Competence, confirmation.OfficialTotal, userId, now);
        db.FinancialCreditCardImportBatches.Add(batch);

        foreach (var item in confirmation.Components.OrderBy(item => item.RowNumber))
        {
            if (item.Classification == "Informational")
            {
                batch.AddItem(FinancialCreditCardImportItem.CreatePdf(familyId, cardId, batch.Id, item.RowNumber, item.ComponentId, item.Type, null, FinancialCreditCardImportItemStatus.Informational, now));
                continue;
            }
            if (item.Classification == "AlreadyExists")
            {
                if (item.ExistingEntityId is not { } existingEntityId || !await ExistingEntityExists(familyId, existingEntityId, cancellationToken))
                    return await AbortConflict(transaction, "Um vínculo existente mudou desde a pré-conciliação.", cancellationToken);
                batch.AddItem(FinancialCreditCardImportItem.CreatePdf(familyId, cardId, batch.Id, item.RowNumber, item.ComponentId, item.Type, item.ExistingEntityId, FinancialCreditCardImportItemStatus.AlreadyExisting, now));
                continue;
            }
            if (item.Classification != "Create" || item.Date is null || item.Amount is null)
                return await AbortInvalid(transaction, "components", "O lote contém componente não elegível para criação.", cancellationToken);

            Guid entityId;
            string entityType;
            if (item.Type is "Purchase" or "InternationalPurchase")
            {
                if (item.CategoryId is not { } categoryId || !await db.FinancialCategories.AnyAsync(category => category.FamilyId == familyId && category.Id == categoryId && category.Type == CategoryType.Expense && category.IsActive, cancellationToken))
                    return await AbortInvalid(transaction, "categories", "A compra exige categoria de despesa ativa da mesma família.", cancellationToken);
                if (item.Amount <= 0m || await ExternalIdentityExists(familyId, cardId, item.ComponentId, cancellationToken))
                    return await AbortConflict(transaction, "A operação financeira mudou desde a pré-conciliação.", cancellationToken);
                var cycle = CreditCardInvoiceCalendarCalculator.ForInstallment(item.Date.Value, 1, card.ClosingDay, card.DueDay);
                if (cycle.ClosingDate != invoice.ClosingDate)
                    return await AbortInvalid(transaction, "invoice", "A compra não pertence ao ciclo validado da fatura.", cancellationToken);
                var purchase = FinancialCreditCardPurchase.Create(familyId, cardId, categoryId, item.Description, item.Date.Value, item.Amount.Value, 1, userId, now);
                purchase.Installments.Single().AssignInvoice(invoice.Id);
                db.FinancialCreditCardPurchases.Add(purchase);
                entityId = purchase.Id;
                entityType = "Purchase";
            }
            else if (item.Type is "Fee" or "Credit" or "Refund")
            {
                if (await ExternalIdentityExists(familyId, cardId, item.ComponentId, cancellationToken))
                    return await AbortConflict(transaction, "A operação financeira mudou desde a pré-conciliação.", cancellationToken);
                if (item.CategoryId is { } adjustmentCategory && !await db.FinancialCategories.AnyAsync(category => category.FamilyId == familyId && category.Id == adjustmentCategory && category.IsActive, cancellationToken))
                    return await AbortInvalid(transaction, "categories", "A categoria do ajuste deve estar ativa e pertencer à mesma família.", cancellationToken);
                var adjustment = FinancialCreditCardInvoiceAdjustment.Create(familyId, cardId, invoice.Id,
                    Enum.Parse<FinancialCreditCardInvoiceAdjustmentType>(item.Type),
                    Enum.Parse<FinancialCreditCardInvoiceAdjustmentNature>(item.Nature), item.Date.Value,
                    item.Description, Math.Abs(item.Amount.Value), item.ComponentId, item.CategoryId, userId, now);
                db.FinancialCreditCardInvoiceAdjustments.Add(adjustment);
                entityId = adjustment.Id;
                entityType = item.Type;
            }
            else return await AbortInvalid(transaction, "components", "Tipo de componente não suportado na confirmação.", cancellationToken);
            batch.AddItem(FinancialCreditCardImportItem.CreatePdf(familyId, cardId, batch.Id, item.RowNumber, item.ComponentId, entityType, entityId, FinancialCreditCardImportItemStatus.Imported, now));
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            var persisted = await OutstandingAsync(familyId, invoice.Id, cancellationToken);
            if (persisted != confirmation.OfficialTotal)
            {
                await transaction.RollbackAsync(cancellationToken);
                db.ChangeTracker.Clear();
                return new(CreditCardPdfConfirmationAccess.Conflict, null, null, $"A composição persistida diverge do documento em {(persisted - confirmation.OfficialTotal).ToString("0.00", CultureInfo.InvariantCulture)}.");
            }
            await transaction.CommitAsync(cancellationToken);
            return new(CreditCardPdfConfirmationAccess.Allowed, ToResult(batch, persisted));
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            var raced = await db.FinancialCreditCardImportBatches.Include(value => value.Items).SingleOrDefaultAsync(value => value.FamilyId == familyId && value.CreditCardId == cardId && value.Source == FinancialCreditCardImportSource.Pdf && value.DocumentHash == confirmation.DocumentHash, cancellationToken);
            if (raced is not null && raced.DecisionHash == confirmation.DecisionHash) return new(CreditCardPdfConfirmationAccess.Allowed, await ResultAsync(raced, confirmation.InvoiceId, cancellationToken));
            return new(CreditCardPdfConfirmationAccess.Conflict, null, null, "O documento ou um componente foi confirmado simultaneamente.");
        }
    }

    private async Task<bool> ExternalIdentityExists(Guid familyId, Guid cardId, string componentId, CancellationToken cancellationToken) =>
        await db.FinancialCreditCardImportItems.AnyAsync(item => item.FamilyId == familyId && item.CreditCardId == cardId && item.ExternalId == componentId, cancellationToken)
        || await db.FinancialCreditCardInvoiceAdjustments.AnyAsync(item => item.FamilyId == familyId && item.CreditCardId == cardId && item.ExternalId == componentId, cancellationToken);

    private async Task<bool> ExistingEntityExists(Guid familyId, Guid entityId, CancellationToken cancellationToken) =>
        await db.FinancialCreditCardInstallments.AnyAsync(item => item.FamilyId == familyId && item.Id == entityId, cancellationToken)
        || await db.FinancialCreditCardImportedInstallments.AnyAsync(item => item.FamilyId == familyId && item.Id == entityId, cancellationToken)
        || await db.FinancialCreditCardInvoiceAdjustments.AnyAsync(item => item.FamilyId == familyId && item.Id == entityId, cancellationToken)
        || await db.FinancialCreditCardPurchases.AnyAsync(item => item.FamilyId == familyId && item.Id == entityId, cancellationToken);

    private async Task<decimal> OutstandingAsync(Guid familyId, Guid invoiceId, CancellationToken cancellationToken) =>
        Math.Max(0m, await FinancialCreditCardInvoiceTotals.TotalAsync(db, familyId, invoiceId, cancellationToken)
            - await FinancialCreditCardInvoiceTotals.PaidAsync(db, familyId, invoiceId, cancellationToken));

    private async Task<CreditCardPdfConfirmationResult> ResultAsync(FinancialCreditCardImportBatch batch, Guid invoiceId, CancellationToken cancellationToken) =>
        ToResult(batch, await OutstandingAsync(batch.FamilyId, invoiceId, cancellationToken));

    private static CreditCardPdfConfirmationResult ToResult(FinancialCreditCardImportBatch batch, decimal persisted)
    {
        var items = batch.Items.OrderBy(item => item.RowNumber).Select(item => new CreditCardPdfConfirmationItem(item.ComponentId!, item.ComponentType!, item.Status.ToString(), item.EntityId)).ToList();
        var official = batch.OfficialTotal ?? 0m;
        return new(batch.Id, $"pdf-sha256:{batch.DocumentHash}", batch.Status.ToString(), batch.Parser!, batch.Competence,
            items.Count(item => item.Type == "Purchase" && item.Result == "Imported"), 0,
            items.Count(item => item.Type is "Fee" or "Credit" or "Refund" && item.Result == "Imported"),
            items.Count(item => item.Result == "AlreadyExisting"), items.Count(item => item.Result == "Informational"), items,
            new(official.ToString("0.00", CultureInfo.InvariantCulture), persisted.ToString("0.00", CultureInfo.InvariantCulture), (persisted - official).ToString("0.00", CultureInfo.InvariantCulture)));
    }

    private static async Task<CreditCardPdfConfirmationOperationResult> AbortInvalid(IDbContextTransaction transaction, string field, string message, CancellationToken cancellationToken)
    { await transaction.RollbackAsync(cancellationToken); return new(CreditCardPdfConfirmationAccess.Invalid, null, new Dictionary<string, string[]> { [field] = [message] }); }
    private static async Task<CreditCardPdfConfirmationOperationResult> AbortConflict(IDbContextTransaction transaction, string message, CancellationToken cancellationToken)
    { await transaction.RollbackAsync(cancellationToken); return new(CreditCardPdfConfirmationAccess.Conflict, null, null, message); }
}
