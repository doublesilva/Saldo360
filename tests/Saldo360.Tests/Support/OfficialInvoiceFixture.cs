using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.CreditCards;
using Saldo360.Infrastructure.CreditCards;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Tests.Support;

/// <summary>Explicit synthetic official evidence for payment/reset tests (the official-statement import was retired in R00; the invoice guards remain). Never used by application code.</summary>
internal static class OfficialInvoiceFixture
{
    private static string Hash(string value) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    internal static async Task CloseAsync(Saldo360DbContext db, Guid invoiceId)
    {
        var invoice = await db.FinancialCreditCardInvoices.SingleAsync(i => i.Id == invoiceId);
        if (invoice.OfficialVersion > 0) return;
        var owner = await db.FinancialWorkspaces.Where(w => w.Id == invoice.WorkspaceId).Select(w => w.OwnerUserId).SingleAsync();
        var total = await FinancialCreditCardInvoiceTotals.TotalAsync(db, invoice.WorkspaceId, invoice.Id, default);
        var snapshot = $"{{\"invoice\":\"{invoice.Id}\",\"total\":\"{total:0.00}\"}}";
        var hash = Hash("synthetic-test-proof:" + invoice.Id);
        db.Add(FinancialCreditCardInvoiceConsolidation.Create(invoice.WorkspaceId, invoice.CreditCardId, invoice.Id, null, hash, hash,
            Hash(snapshot), System.Text.Json.JsonSerializer.Serialize(new { SyntheticTestEvidence = true, Composition = snapshot }), 1, total, owner, DateTimeOffset.UtcNow));
        invoice.CloseOfficial(1, total, DateTimeOffset.UtcNow); await db.SaveChangesAsync();
    }
}
