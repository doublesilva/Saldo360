using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.CreditCards;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.CreditCards;

internal sealed class FinancialCreditCardImportedInstallmentRepository(Saldo360DbContext db) : IFinancialCreditCardImportedInstallmentRepository
{
    public async Task<IReadOnlyList<ImportedInstallmentMatchDto>?> FindMatchesAsync(Guid familyId, Guid cardId, DateOnly date, decimal amount, int number, int count, string? competence, string description, CancellationToken cancellationToken)
    {
        if (!await db.FinancialCreditCards.AnyAsync(card => card.FamilyId == familyId && card.Id == cardId && !card.IsArchived, cancellationToken)) return null;
        var query = from installment in db.FinancialCreditCardInstallments.AsNoTracking()
                    join purchase in db.FinancialCreditCardPurchases.AsNoTracking() on new { FamilyId = installment.FamilyId, PurchaseId = installment.PurchaseId } equals new { FamilyId = purchase.FamilyId, PurchaseId = purchase.Id }
                    join invoice in db.FinancialCreditCardInvoices.AsNoTracking() on new { installment.FamilyId, InvoiceId = installment.InvoiceId!.Value } equals new { invoice.FamilyId, InvoiceId = invoice.Id }
                    where installment.FamilyId == familyId && purchase.CreditCardId == cardId && installment.Status == CreditCardInstallmentStatus.Active && purchase.Status == CreditCardPurchaseStatus.Active
                        && installment.InstallmentNumber == number && purchase.InstallmentCount == count && installment.Amount == amount && purchase.PurchaseDate == date
                        && (competence == null || invoice.Competence == competence)
                    select new { InstallmentId = installment.Id, InvoiceId = invoice.Id, purchase.Description, installment.Amount, installment.InstallmentNumber, purchase.InstallmentCount, invoice.Competence, purchase.PurchaseDate };
        var rows = await query.ToListAsync(cancellationToken);
        return rows.Select(row => new ImportedInstallmentMatchDto(row.InstallmentId, row.InvoiceId, row.Description, row.Amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), row.InstallmentNumber, row.InstallmentCount, row.Competence, row.PurchaseDate.ToString("yyyy-MM-dd"), row.Description.Contains(description, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    public async Task<ImportedInstallmentOperationResult> LinkAsync(Guid familyId, Guid cardId, Guid userId, ImportedInstallmentInput input, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!await db.FinancialCreditCards.AnyAsync(card => card.FamilyId == familyId && card.Id == cardId && !card.IsArchived, cancellationToken))
            return new(ImportedInstallmentAccess.NotFound, null, null, null);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var existing = await FindExistingAsync(familyId, cardId, input, cancellationToken);
        if (existing is not null) return SameOrConflict(existing, input);
        var target = await (from installment in db.FinancialCreditCardInstallments
                            join purchase in db.FinancialCreditCardPurchases on new { installment.FamilyId, installment.PurchaseId } equals new { purchase.FamilyId, PurchaseId = purchase.Id }
                            join invoice in db.FinancialCreditCardInvoices on new { installment.FamilyId, InvoiceId = installment.InvoiceId!.Value } equals new { invoice.FamilyId, InvoiceId = invoice.Id }
                            where installment.FamilyId == familyId && purchase.CreditCardId == cardId && installment.Id == input.InstallmentId && installment.Status == CreditCardInstallmentStatus.Active && purchase.Status == CreditCardPurchaseStatus.Active
                            select new { installment, purchase, invoice }).SingleOrDefaultAsync(cancellationToken);
        if (target is null) { await transaction.RollbackAsync(cancellationToken); return new(ImportedInstallmentAccess.NotFound, null, null, new Dictionary<string, string[]> { ["installmentId"] = ["Parcela não encontrada para este cartão."] }); }
        if (target.installment.InstallmentNumber != input.InstallmentNumber || target.purchase.InstallmentCount != input.InstallmentCount || target.installment.Amount != decimal.Parse(input.Amount, System.Globalization.CultureInfo.InvariantCulture) || (input.Competence is not null && target.invoice.Competence != input.Competence))
        { await transaction.RollbackAsync(cancellationToken); return Invalid("installment", "A parcela selecionada não corresponde exatamente aos dados informados."); }
        if (await db.FinancialCreditCardImportedInstallments.AnyAsync(item => item.FamilyId == familyId && item.CreditCardId == cardId && item.LinkedInstallmentId == target.installment.Id && item.Status == FinancialCreditCardImportedInstallmentStatus.Active, cancellationToken))
        { await transaction.RollbackAsync(cancellationToken); return Invalid("installmentId", "A parcela já possui uma associação de importação ativa."); }
        if (await ExternalExistsAsync(familyId, cardId, input.ExternalId, cancellationToken)) { await transaction.RollbackAsync(cancellationToken); return Invalid("externalId", "O identificador externo já foi utilizado."); }
        var record = FinancialCreditCardImportedInstallment.Create(familyId, cardId, input.ImportId, input.RowNumber, input.ExternalId, target.installment.Id, target.invoice.Id, DateOnly.Parse(input.TransactionDate), input.Description, decimal.Parse(input.Amount, System.Globalization.CultureInfo.InvariantCulture), input.InstallmentNumber, input.InstallmentCount, input.CategoryId, false, userId, now);
        db.FinancialCreditCardImportedInstallments.Add(record);
        try { await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); return new(ImportedInstallmentAccess.Allowed, ToDto(record), null, null); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }) { await transaction.RollbackAsync(cancellationToken); db.ChangeTracker.Clear(); return new(ImportedInstallmentAccess.Conflict, null, null, null, "A associação já foi criada simultaneamente."); }
    }

    public async Task<ImportedInstallmentOperationResult> CreateHistoricalAsync(Guid familyId, Guid cardId, Guid userId, ImportedInstallmentInput input, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!await db.FinancialCreditCards.AnyAsync(card => card.FamilyId == familyId && card.Id == cardId && !card.IsArchived, cancellationToken))
            return new(ImportedInstallmentAccess.NotFound, null, null, null);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var existing = await FindExistingAsync(familyId, cardId, input, cancellationToken);
        if (existing is not null) return SameOrConflict(existing, input);
        var invoice = await db.FinancialCreditCardInvoices.SingleOrDefaultAsync(item => item.FamilyId == familyId && item.CreditCardId == cardId && item.Competence == input.Competence, cancellationToken);
        if (invoice is null) { await transaction.RollbackAsync(cancellationToken); return Invalid("competence", "Não existe fatura correspondente à competência informada."); }
        var paid = await db.FinancialCreditCardInvoicePayments.Where(item => item.FamilyId == familyId && item.InvoiceId == invoice.Id && item.Status == FinancialCreditCardInvoicePaymentStatus.Active).SumAsync(item => (decimal?)item.Amount, cancellationToken) ?? 0m;
        var current = await InvoiceTotalAsync(invoice.Id, cancellationToken);
        if (paid >= current && current > 0m) { await transaction.RollbackAsync(cancellationToken); return Invalid("invoiceId", "A fatura já está quitada e não pode receber obrigação histórica silenciosamente."); }
        if (await ExternalExistsAsync(familyId, cardId, input.ExternalId, cancellationToken)) { await transaction.RollbackAsync(cancellationToken); return Invalid("externalId", "O identificador externo já foi utilizado."); }
        var record = FinancialCreditCardImportedInstallment.Create(familyId, cardId, input.ImportId, input.RowNumber, input.ExternalId, null, invoice.Id, DateOnly.Parse(input.TransactionDate), input.Description, decimal.Parse(input.Amount, System.Globalization.CultureInfo.InvariantCulture), input.InstallmentNumber, input.InstallmentCount, input.CategoryId, true, userId, now);
        db.FinancialCreditCardImportedInstallments.Add(record);
        try { await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); return new(ImportedInstallmentAccess.Allowed, ToDto(record), null, null); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }) { await transaction.RollbackAsync(cancellationToken); db.ChangeTracker.Clear(); return new(ImportedInstallmentAccess.Conflict, null, null, null, "A parcela histórica já foi registrada simultaneamente."); }
    }

    public async Task<IReadOnlyList<ImportedInstallmentRecordDto>?> ListAsync(Guid familyId, Guid cardId, CancellationToken cancellationToken) =>
        !await db.FinancialCreditCards.AnyAsync(item => item.FamilyId == familyId && item.Id == cardId, cancellationToken) ? null : (await db.FinancialCreditCardImportedInstallments.AsNoTracking().Where(item => item.FamilyId == familyId && item.CreditCardId == cardId).OrderBy(item => item.CreatedAt).ToListAsync(cancellationToken)).Select(ToDto).ToList();

    public async Task<ImportedInstallmentRecordDto?> GetAsync(Guid familyId, Guid cardId, Guid id, CancellationToken cancellationToken) =>
        (await db.FinancialCreditCardImportedInstallments.AsNoTracking().SingleOrDefaultAsync(item => item.FamilyId == familyId && item.CreditCardId == cardId && item.Id == id, cancellationToken)) is { } record ? ToDto(record) : null;

    public async Task<bool> CancelAsync(Guid familyId, Guid cardId, Guid id, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var record = await db.FinancialCreditCardImportedInstallments.SingleOrDefaultAsync(item => item.FamilyId == familyId && item.CreditCardId == cardId && item.Id == id && item.Status == FinancialCreditCardImportedInstallmentStatus.Active, cancellationToken);
        if (record is null || !record.IsHistorical) return false;
        var paid = await db.FinancialCreditCardInvoicePayments.Where(item => item.FamilyId == familyId && item.InvoiceId == record.InvoiceId && item.Status == FinancialCreditCardInvoicePaymentStatus.Active).SumAsync(item => (decimal?)item.Amount, cancellationToken) ?? 0m;
        var total = await InvoiceTotalAsync(record.InvoiceId, cancellationToken);
        if (paid > total - record.Amount) return false;
        record.Cancel(userId, now); await db.SaveChangesAsync(cancellationToken); return true;
    }

    public Task<bool> IsExpenseCategoryAsync(Guid familyId, Guid categoryId, CancellationToken cancellationToken) => db.FinancialCategories.AnyAsync(item => item.FamilyId == familyId && item.Id == categoryId && item.Type == Domain.Categories.CategoryType.Expense && item.IsActive, cancellationToken);

    private async Task<FinancialCreditCardImportedInstallment?> FindExistingAsync(Guid familyId, Guid cardId, ImportedInstallmentInput input, CancellationToken cancellationToken) => await db.FinancialCreditCardImportedInstallments.SingleOrDefaultAsync(item => item.FamilyId == familyId && item.CreditCardId == cardId && item.ImportId == input.ImportId && item.RowNumber == input.RowNumber, cancellationToken);
    private async Task<bool> ExternalExistsAsync(Guid familyId, Guid cardId, string? externalId, CancellationToken cancellationToken) => externalId is not null && await db.FinancialCreditCardImportedInstallments.AnyAsync(item => item.FamilyId == familyId && item.CreditCardId == cardId && item.ExternalId == externalId && item.Status == FinancialCreditCardImportedInstallmentStatus.Active, cancellationToken);
    private async Task<decimal> InvoiceTotalAsync(Guid invoiceId, CancellationToken cancellationToken) => (await db.FinancialCreditCardInstallments.Where(item => item.InvoiceId == invoiceId && item.Status == CreditCardInstallmentStatus.Active).SumAsync(item => (decimal?)item.Amount, cancellationToken) ?? 0m) + (await db.FinancialCreditCardImportedInstallments.Where(item => item.InvoiceId == invoiceId && item.IsHistorical && item.Status == FinancialCreditCardImportedInstallmentStatus.Active).SumAsync(item => (decimal?)item.Amount, cancellationToken) ?? 0m);
    private static ImportedInstallmentOperationResult Invalid(string field, string message) => new(ImportedInstallmentAccess.Invalid, null, null, new Dictionary<string, string[]> { [field] = [message] });
    private static ImportedInstallmentOperationResult SameOrConflict(FinancialCreditCardImportedInstallment existing, ImportedInstallmentInput input) =>
        existing.TransactionDate.ToString("yyyy-MM-dd") == input.TransactionDate
        && existing.Amount == decimal.Parse(input.Amount, System.Globalization.CultureInfo.InvariantCulture)
        && existing.Description == input.Description
        && existing.InstallmentNumber == input.InstallmentNumber
        && existing.InstallmentCount == input.InstallmentCount
        && existing.ExternalId == input.ExternalId
        && existing.CategoryId == input.CategoryId
        && existing.IsHistorical == (existing.LinkedInstallmentId is null)
        && (input.InstallmentId is null || existing.LinkedInstallmentId == input.InstallmentId)
            ? new(ImportedInstallmentAccess.Allowed, ToDto(existing), null, null)
            : new(ImportedInstallmentAccess.Conflict, null, null, null, "A mesma operação e linha foram enviadas com conteúdo diferente.");
    private static ImportedInstallmentRecordDto ToDto(FinancialCreditCardImportedInstallment item) => new(item.Id, item.ImportId, item.RowNumber, item.InvoiceId, item.LinkedInstallmentId, item.TransactionDate.ToString("yyyy-MM-dd"), item.Description, item.Amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), item.InstallmentNumber, item.InstallmentCount, item.ExternalId, item.CategoryId?.ToString(), item.IsHistorical, item.Status.ToString(), item.CreatedAt.ToString("O"), item.CancelledAt?.ToString("O"));
}
