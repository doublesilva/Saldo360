using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.CreditCards;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.CreditCards;

internal static class FinancialCreditCardInvoiceTotals
{
    public static async Task<decimal> TotalAsync(
        Saldo360DbContext db, Guid workspaceId, Guid invoiceId, CancellationToken cancellationToken)
    {
        var installments = await db.FinancialCreditCardInstallments.AsNoTracking()
            .Where(item => item.WorkspaceId == workspaceId && item.InvoiceId == invoiceId && item.Status == CreditCardInstallmentStatus.Active)
            .SumAsync(item => (decimal?)item.Amount, cancellationToken) ?? 0m;
        var historical = await db.FinancialCreditCardImportedInstallments.AsNoTracking()
            .Where(item => item.WorkspaceId == workspaceId && item.InvoiceId == invoiceId && item.IsHistorical && item.Status == FinancialCreditCardImportedInstallmentStatus.Active)
            .SumAsync(item => (decimal?)item.Amount, cancellationToken) ?? 0m;
        var adjustments = await db.FinancialCreditCardInvoiceAdjustments.AsNoTracking()
            .Where(item => item.WorkspaceId == workspaceId && item.InvoiceId == invoiceId && item.Status == FinancialCreditCardInvoiceAdjustmentStatus.Active)
            .SumAsync(item => (decimal?)(item.Nature == FinancialCreditCardInvoiceAdjustmentNature.Debit ? item.Amount : -item.Amount), cancellationToken) ?? 0m;
        return installments + historical + adjustments;
    }

    public static async Task<decimal> PaidAsync(
        Saldo360DbContext db, Guid workspaceId, Guid invoiceId, CancellationToken cancellationToken) =>
        await db.FinancialCreditCardInvoicePayments.AsNoTracking()
            .Where(item => item.WorkspaceId == workspaceId && item.InvoiceId == invoiceId && item.Status == FinancialCreditCardInvoicePaymentStatus.Active)
            .SumAsync(item => (decimal?)item.Amount, cancellationToken) ?? 0m;
}
