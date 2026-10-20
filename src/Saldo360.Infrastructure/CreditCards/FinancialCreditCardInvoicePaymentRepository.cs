using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Common;
using Saldo360.Domain.Transactions;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.CreditCards;

internal sealed class FinancialCreditCardInvoicePaymentRepository(Saldo360DbContext db) : IFinancialCreditCardInvoicePaymentRepository
{
    public async Task<InvoicePaymentSnapshot?> GetSnapshotAsync(Guid workspaceId, Guid cardId, Guid invoiceId, DateOnly today, CancellationToken cancellationToken)
    {
        var invoice = await db.FinancialCreditCardInvoices.AsNoTracking().Where(i => i.WorkspaceId == workspaceId && i.CreditCardId == cardId && i.Id == invoiceId).Select(i => new { i.Id, i.ClosingDate, i.CreditCardId }).SingleOrDefaultAsync(cancellationToken);
        if (invoice is null) return null;
        var total = await FinancialCreditCardInvoiceTotals.TotalAsync(db, workspaceId, invoiceId, cancellationToken);
        var paid = await db.FinancialCreditCardInvoicePayments.AsNoTracking().Where(p => p.WorkspaceId == workspaceId && p.InvoiceId == invoiceId && p.Status == FinancialCreditCardInvoicePaymentStatus.Active).SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;
        var card = await db.FinancialCreditCards.AsNoTracking().Where(c => c.WorkspaceId == workspaceId && c.Id == cardId).Select(c => new { c.DefaultPaymentAccountId }).SingleAsync(cancellationToken);
        var account = card.DefaultPaymentAccountId is { } accountId ? await db.FinancialAccounts.AsNoTracking().Where(a => a.WorkspaceId == workspaceId && a.Id == accountId).Select(a => new { a.IsActive }).SingleOrDefaultAsync(cancellationToken) : null;
        return new(invoice.Id, invoice.ClosingDate, total, paid, card.DefaultPaymentAccountId, account is not null, account?.IsActive == true);
    }

    public async Task<IReadOnlyList<FinancialCreditCardInvoicePaymentDto>?> ListAsync(Guid workspaceId, Guid cardId, Guid invoiceId, CancellationToken cancellationToken)
    {
        if (!await db.FinancialCreditCardInvoices.AnyAsync(i => i.WorkspaceId == workspaceId && i.CreditCardId == cardId && i.Id == invoiceId, cancellationToken)) return null;
        return await db.FinancialCreditCardInvoicePayments.AsNoTracking().Where(p => p.WorkspaceId == workspaceId && p.InvoiceId == invoiceId).OrderBy(p => p.PaymentDate).ThenBy(p => p.Id).Select(Project).ToListAsync(cancellationToken);
    }

    public async Task<FinancialCreditCardInvoicePaymentDto?> GetAsync(Guid workspaceId, Guid cardId, Guid invoiceId, Guid paymentId, CancellationToken cancellationToken)
    {
        if (!await db.FinancialCreditCardInvoices.AnyAsync(i => i.WorkspaceId == workspaceId && i.CreditCardId == cardId && i.Id == invoiceId, cancellationToken)) return null;
        return await db.FinancialCreditCardInvoicePayments.AsNoTracking().Where(p => p.WorkspaceId == workspaceId && p.InvoiceId == invoiceId && p.Id == paymentId).Select(Project).SingleOrDefaultAsync(cancellationToken);
    }

    public Task<FinancialCreditCardInvoicePaymentDto?> GetByOperationAsync(Guid workspaceId, Guid operationId, CancellationToken cancellationToken) =>
        db.FinancialCreditCardInvoicePayments.AsNoTracking()
            .Where(p => p.WorkspaceId == workspaceId && p.OperationId == operationId)
            .Select(Project).SingleOrDefaultAsync(cancellationToken);

    public Task<bool> PayloadMatchesAsync(Guid workspaceId, Guid operationId, Guid invoiceId, Guid accountId, DateOnly paymentDate, decimal amount, CancellationToken cancellationToken) =>
        db.FinancialCreditCardInvoicePayments.AsNoTracking().AnyAsync(p =>
            p.WorkspaceId == workspaceId && p.OperationId == operationId && p.InvoiceId == invoiceId
            && p.FinancialAccountId == accountId && p.PaymentDate == paymentDate && p.Amount == amount,
            cancellationToken);

    public async Task<PaymentCreationResult> CreateAsync(Guid workspaceId, Guid cardId, Guid invoiceId, Guid operationId, Guid accountId, DateOnly paymentDate, decimal amount, Guid userId, DateOnly today, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var invoice = await db.FinancialCreditCardInvoices.SingleOrDefaultAsync(i => i.WorkspaceId == workspaceId && i.CreditCardId == cardId && i.Id == invoiceId, cancellationToken);
        if (invoice is null) return new(PaymentOperationResult.NotFound);
        if (today <= invoice.ClosingDate) return new(PaymentOperationResult.OpenInvoice);
        var account = await db.FinancialAccounts.SingleOrDefaultAsync(a => a.WorkspaceId == workspaceId && a.Id == accountId, cancellationToken);
        if (account is null || !account.IsActive) return new(PaymentOperationResult.InvalidAccount);
        var total = await FinancialCreditCardInvoiceTotals.TotalAsync(db, workspaceId, invoiceId, cancellationToken);
        var paid = await db.FinancialCreditCardInvoicePayments.Where(p => p.WorkspaceId == workspaceId && p.InvoiceId == invoiceId && p.Status == FinancialCreditCardInvoicePaymentStatus.Active).SumAsync(p => (decimal?)p.Amount, cancellationToken) ?? 0m;
        if (total <= 0m || amount > total - paid) return new(PaymentOperationResult.ExceedsDue);
        var transaction = FinancialTransaction.CreateInvoicePayment(workspaceId, accountId, $"Pagamento de fatura {invoice.Competence}", amount, paymentDate, userId, now);
        var payment = FinancialCreditCardInvoicePayment.Create(workspaceId, invoiceId, accountId, transaction.Id, operationId, paymentDate, amount, userId, now);
        db.FinancialTransactions.Add(transaction); db.FinancialCreditCardInvoicePayments.Add(payment);
        try
        {
            await db.SaveChangesAsync(cancellationToken); await tx.CommitAsync(cancellationToken);
            return new(PaymentOperationResult.Success, payment.Id);
        }
        catch (Exception exception) when (IsConcurrencyConflict(exception))
        {
            await tx.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            return new(PaymentOperationResult.DuplicateOperation);
        }
    }

    public async Task<bool> CancelAsync(Guid workspaceId, Guid cardId, Guid invoiceId, Guid paymentId, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var payment = await (from p in db.FinancialCreditCardInvoicePayments where p.WorkspaceId == workspaceId && p.InvoiceId == invoiceId && p.Id == paymentId && db.FinancialCreditCardInvoices.Any(i => i.WorkspaceId == workspaceId && i.CreditCardId == cardId && i.Id == invoiceId) select p).SingleOrDefaultAsync(cancellationToken);
        if (payment is null) return false;
        if (!payment.Cancel(userId, now)) return true;
        var transaction = await db.FinancialTransactions.SingleOrDefaultAsync(t => t.WorkspaceId == workspaceId && t.Id == payment.TransactionId, cancellationToken);
        transaction?.Cancel(userId, now);
        await db.SaveChangesAsync(cancellationToken); await tx.CommitAsync(cancellationToken); return true;
    }

    private static readonly System.Linq.Expressions.Expression<Func<FinancialCreditCardInvoicePayment, FinancialCreditCardInvoicePaymentDto>> Project = p =>
        new(p.Id, p.InvoiceId, p.FinancialAccountId, p.TransactionId, p.OperationId, p.PaymentDate, Money.Format(p.Amount), p.Status, p.CreatedAt, p.CancelledAt);

    private static bool IsConcurrencyConflict(Exception exception) =>
        exception is PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.UniqueViolation }
        || exception.InnerException is not null && IsConcurrencyConflict(exception.InnerException);
}
