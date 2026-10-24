using Microsoft.EntityFrameworkCore;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.CreditCards;
using Saldo360.Infrastructure.CreditCards;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Tests.Support;

/// <summary>Explicit synthetic official evidence for legacy payment/reset tests. Never used by application code.</summary>
internal static class OfficialInvoiceFixture
{
    internal static async Task CloseAsync(Saldo360DbContext db, Guid invoiceId)
    {
        var invoice = await db.FinancialCreditCardInvoices.SingleAsync(i => i.Id == invoiceId);
        if (invoice.OfficialVersion > 0) return;
        var owner = await db.FinancialWorkspaces.Where(w => w.Id == invoice.WorkspaceId).Select(w => w.OwnerUserId).SingleAsync();
        var total = await FinancialCreditCardInvoiceTotals.TotalAsync(db, invoice.WorkspaceId, invoice.Id, default);
        var snapshot = await new CreditCardPdfConfirmationRepository(db).CompositionSnapshotAsync(invoice.WorkspaceId, invoice.CreditCardId, invoice.Id, invoice.Competence, default);
        var hash = OfficialStatementAnalysisValidation.Hash("synthetic-test-proof:" + invoice.Id);
        db.Add(FinancialCreditCardInvoiceConsolidation.Create(invoice.WorkspaceId, invoice.CreditCardId, invoice.Id, null, hash, hash,
            OfficialStatementAnalysisValidation.Hash(snapshot), System.Text.Json.JsonSerializer.Serialize(new { SyntheticTestEvidence = true, Composition = snapshot }), 1, total, owner, DateTimeOffset.UtcNow));
        invoice.CloseOfficial(1, total, DateTimeOffset.UtcNow); await db.SaveChangesAsync();
    }
}
