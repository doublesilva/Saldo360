using System.Data;
using Microsoft.EntityFrameworkCore;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Common;
using Saldo360.Domain.Transactions;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.CreditCards;

internal sealed class FinancialCreditCardInvoicePaymentRepository(Saldo360DbContext db) : IFinancialCreditCardInvoicePaymentRepository
{
    public async Task<InvoicePaymentSnapshot?> GetSnapshotAsync(Guid familyId, Guid cardId, Guid invoiceId, DateOnly today, CancellationToken cancellationToken)
    {
        var invoice = await db.FinancialCreditCardInvoices.AsNoTracking().Where(i => i.FamilyId == familyId && i.CreditCardId == cardId && i.Id == invoiceId).Select(i => new { i.Id, i.ClosingDate, i.CreditCardId }).SingleOrDefaultAsync(cancellationToken);
        if (invoice is null) return null;
        var total = await db.FinancialCreditCardInstallments.AsNoTracking().Where(i => i.FamilyId == familyId && i.InvoiceId == invoiceId && i.Status == CreditCardInstallmentStatus.Active).SumAsync(i => (decimal?)i.Amount, cancellationToken) ?? 0m;
        var paid = await db.FinancialCreditCardInvoicePayments.AsNoTracking().Where(p => p.FamilyId == familyId && p.InvoiceId == invoiceId && p.Status == FinancialCreditCardInvoicePaymentStatus.Active).SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;
        var card = await db.FinancialCreditCards.AsNoTracking().Where(c => c.FamilyId == familyId && c.Id == cardId).Select(c => new { c.DefaultPaymentAccountId }).SingleAsync(cancellationToken);
        var account = card.DefaultPaymentAccountId is { } accountId ? await db.FinancialAccounts.AsNoTracking().Where(a => a.FamilyId == familyId && a.Id == accountId).Select(a => new { a.IsActive }).SingleOrDefaultAsync(cancellationToken) : null;
        return new(invoice.Id, invoice.ClosingDate, total, paid, card.DefaultPaymentAccountId, account is not null, account?.IsActive == true);
    }

    public async Task<IReadOnlyList<FinancialCreditCardInvoicePaymentDto>?> ListAsync(Guid familyId, Guid cardId, Guid invoiceId, CancellationToken cancellationToken)
    {
        if (!await db.FinancialCreditCardInvoices.AnyAsync(i => i.FamilyId == familyId && i.CreditCardId == cardId && i.Id == invoiceId, cancellationToken)) return null;
        return await db.FinancialCreditCardInvoicePayments.AsNoTracking().Where(p => p.FamilyId == familyId && p.InvoiceId == invoiceId).OrderBy(p => p.PaymentDate).ThenBy(p => p.Id).Select(p => new FinancialCreditCardInvoicePaymentDto(p.Id, p.InvoiceId, p.FinancialAccountId, p.TransactionId, p.PaymentDate, Money.Format(p.Amount), p.Status, p.CreatedAt, p.CancelledAt)).ToListAsync(cancellationToken);
    }

    public async Task<FinancialCreditCardInvoicePaymentDto?> GetAsync(Guid familyId, Guid cardId, Guid invoiceId, Guid paymentId, CancellationToken cancellationToken)
    {
        if (!await db.FinancialCreditCardInvoices.AnyAsync(i => i.FamilyId == familyId && i.CreditCardId == cardId && i.Id == invoiceId, cancellationToken)) return null;
        return await db.FinancialCreditCardInvoicePayments.AsNoTracking().Where(p => p.FamilyId == familyId && p.InvoiceId == invoiceId && p.Id == paymentId).Select(p => new FinancialCreditCardInvoicePaymentDto(p.Id, p.InvoiceId, p.FinancialAccountId, p.TransactionId, p.PaymentDate, Money.Format(p.Amount), p.Status, p.CreatedAt, p.CancelledAt)).SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<PaymentCreationResult> CreateAsync(Guid familyId, Guid cardId, Guid invoiceId, Guid accountId, DateOnly paymentDate, decimal amount, Guid userId, DateOnly today, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var invoice = await db.FinancialCreditCardInvoices.SingleOrDefaultAsync(i => i.FamilyId == familyId && i.CreditCardId == cardId && i.Id == invoiceId, cancellationToken);
        if (invoice is null) return new(PaymentOperationResult.NotFound);
        if (today <= invoice.ClosingDate) return new(PaymentOperationResult.OpenInvoice);
        var account = await db.FinancialAccounts.SingleOrDefaultAsync(a => a.FamilyId == familyId && a.Id == accountId, cancellationToken);
        if (account is null || !account.IsActive) return new(PaymentOperationResult.InvalidAccount);
        var total = await db.FinancialCreditCardInstallments.Where(i => i.FamilyId == familyId && i.InvoiceId == invoiceId && i.Status == CreditCardInstallmentStatus.Active).SumAsync(i => (decimal?)i.Amount, cancellationToken) ?? 0m;
        var paid = await db.FinancialCreditCardInvoicePayments.Where(p => p.FamilyId == familyId && p.InvoiceId == invoiceId && p.Status == FinancialCreditCardInvoicePaymentStatus.Active).SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;
        if (total <= 0m || amount > total - paid) return new(PaymentOperationResult.ExceedsDue);
        var transaction = FinancialTransaction.CreateInvoicePayment(familyId, accountId, $"Pagamento de fatura {invoice.Competence}", amount, paymentDate, userId, now);
        var payment = FinancialCreditCardInvoicePayment.Create(familyId, invoiceId, accountId, transaction.Id, paymentDate, amount, userId, now);
        db.FinancialTransactions.Add(transaction); db.FinancialCreditCardInvoicePayments.Add(payment);
        await db.SaveChangesAsync(cancellationToken); await tx.CommitAsync(cancellationToken);
        return new(PaymentOperationResult.Success, payment.Id);
    }

    public async Task<bool> CancelAsync(Guid familyId, Guid cardId, Guid invoiceId, Guid paymentId, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var payment = await (from p in db.FinancialCreditCardInvoicePayments where p.FamilyId == familyId && p.InvoiceId == invoiceId && p.Id == paymentId && db.FinancialCreditCardInvoices.Any(i => i.FamilyId == familyId && i.CreditCardId == cardId && i.Id == invoiceId) select p).SingleOrDefaultAsync(cancellationToken);
        if (payment is null) return false;
        if (!payment.Cancel(userId, now)) return true;
        var transaction = await db.FinancialTransactions.SingleOrDefaultAsync(t => t.FamilyId == familyId && t.Id == payment.TransactionId, cancellationToken);
        transaction?.Cancel(userId, now);
        await db.SaveChangesAsync(cancellationToken); await tx.CommitAsync(cancellationToken); return true;
    }
}
