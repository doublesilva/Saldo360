using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.CreditCards;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.CreditCards;

internal sealed class FinancialCreditCardInvoiceAdjustmentRepository(Saldo360DbContext db)
    : IFinancialCreditCardInvoiceAdjustmentRepository
{
    public async Task<InvoiceAdjustmentOperationResult> CreateAsync(
        Guid familyId, Guid cardId, Guid invoiceId, Guid userId,
        FinancialCreditCardInvoiceAdjustmentInput input, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var existing = await db.FinancialCreditCardInvoiceAdjustments
            .SingleOrDefaultAsync(item => item.FamilyId == familyId && item.CreditCardId == cardId && item.ExternalId == input.ExternalId, cancellationToken);
        if (existing is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Matches(existing, invoiceId, input)
                ? new(InvoiceAdjustmentAccess.Allowed, ToDto(existing))
                : new(InvoiceAdjustmentAccess.Conflict, null, null, "A identidade externa já existe com conteúdo financeiro diferente.");
        }

        var invoiceExists = await db.FinancialCreditCardInvoices.AnyAsync(
            item => item.FamilyId == familyId && item.CreditCardId == cardId && item.Id == invoiceId, cancellationToken);
        if (!invoiceExists)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(InvoiceAdjustmentAccess.NotFound);
        }
        if (input.CategoryId is { } categoryId && !await db.FinancialCategories.AnyAsync(
                item => item.FamilyId == familyId && item.Id == categoryId && item.IsActive, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return Invalid("categoryId", "A categoria deve estar ativa e pertencer à mesma família.");
        }

        var entity = FinancialCreditCardInvoiceAdjustment.Create(
            familyId, cardId, invoiceId,
            Enum.Parse<FinancialCreditCardInvoiceAdjustmentType>(input.Type, true),
            Enum.Parse<FinancialCreditCardInvoiceAdjustmentNature>(input.Nature, true),
            DateOnly.ParseExact(input.TransactionDate, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            input.Description, decimal.Parse(input.Amount, CultureInfo.InvariantCulture),
            input.ExternalId, input.CategoryId, userId, now);
        db.FinancialCreditCardInvoiceAdjustments.Add(entity);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(InvoiceAdjustmentAccess.Allowed, ToDto(entity));
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            var raced = await db.FinancialCreditCardInvoiceAdjustments.AsNoTracking()
                .SingleOrDefaultAsync(item => item.FamilyId == familyId && item.CreditCardId == cardId && item.ExternalId == input.ExternalId, cancellationToken);
            return raced is not null && Matches(raced, invoiceId, input)
                ? new(InvoiceAdjustmentAccess.Allowed, ToDto(raced))
                : new(InvoiceAdjustmentAccess.Conflict, null, null, "A identidade externa foi utilizada simultaneamente com conteúdo diferente.");
        }
    }

    public async Task<InvoiceAdjustmentOperationResult> CancelAsync(
        Guid familyId, Guid cardId, Guid invoiceId, Guid adjustmentId,
        Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var adjustment = await db.FinancialCreditCardInvoiceAdjustments.SingleOrDefaultAsync(
            item => item.FamilyId == familyId && item.CreditCardId == cardId && item.InvoiceId == invoiceId && item.Id == adjustmentId, cancellationToken);
        if (adjustment is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(InvoiceAdjustmentAccess.NotFound);
        }
        if (adjustment.Status == FinancialCreditCardInvoiceAdjustmentStatus.Cancelled)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(InvoiceAdjustmentAccess.Allowed, ToDto(adjustment));
        }
        var total = await FinancialCreditCardInvoiceTotals.TotalAsync(db, familyId, invoiceId, cancellationToken);
        var paid = await FinancialCreditCardInvoiceTotals.PaidAsync(db, familyId, invoiceId, cancellationToken);
        var totalAfterCancellation = total - adjustment.SignedAmount;
        if (totalAfterCancellation < paid)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(InvoiceAdjustmentAccess.PaidTotalConflict, null, null, "O cancelamento deixaria o total da fatura abaixo do valor já pago.");
        }
        adjustment.Cancel(userId, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(InvoiceAdjustmentAccess.Allowed, ToDto(adjustment));
    }

    public async Task<IReadOnlyList<FinancialCreditCardInvoiceAdjustmentDto>?> ListAsync(
        Guid familyId, Guid cardId, Guid invoiceId, CancellationToken cancellationToken)
    {
        if (!await db.FinancialCreditCardInvoices.AnyAsync(item => item.FamilyId == familyId && item.CreditCardId == cardId && item.Id == invoiceId, cancellationToken)) return null;
        return (await db.FinancialCreditCardInvoiceAdjustments.AsNoTracking()
            .Where(item => item.FamilyId == familyId && item.CreditCardId == cardId && item.InvoiceId == invoiceId)
            .OrderBy(item => item.TransactionDate).ThenBy(item => item.CreatedAt).ToListAsync(cancellationToken))
            .Select(ToDto).ToList();
    }

    private static bool Matches(FinancialCreditCardInvoiceAdjustment entity, Guid invoiceId, FinancialCreditCardInvoiceAdjustmentInput input) =>
        entity.InvoiceId == invoiceId
        && entity.Type == Enum.Parse<FinancialCreditCardInvoiceAdjustmentType>(input.Type, true)
        && entity.Nature == Enum.Parse<FinancialCreditCardInvoiceAdjustmentNature>(input.Nature, true)
        && entity.TransactionDate == DateOnly.ParseExact(input.TransactionDate, "yyyy-MM-dd", CultureInfo.InvariantCulture)
        && entity.Description == input.Description
        && entity.Amount == decimal.Parse(input.Amount, CultureInfo.InvariantCulture)
        && entity.CategoryId == input.CategoryId;

    private static FinancialCreditCardInvoiceAdjustmentDto ToDto(FinancialCreditCardInvoiceAdjustment item) => new(
        item.Id, item.InvoiceId, item.Type.ToString(), item.Nature.ToString(), item.TransactionDate.ToString("yyyy-MM-dd"),
        item.Description, item.Amount.ToString("0.00", CultureInfo.InvariantCulture), item.ExternalId, item.CategoryId,
        item.Status.ToString(), item.CreatedAt.ToString("O"), item.CancelledAt?.ToString("O"));

    private static InvoiceAdjustmentOperationResult Invalid(string field, string message) =>
        new(InvoiceAdjustmentAccess.Invalid, null, new Dictionary<string, string[]> { [field] = [message] });
}
