using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.CreditCards;
using Saldo360.Infrastructure.CreditCards;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.CreditCards;

[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Documents")]
[Trait("Area", "Cards")]
[Trait("Dependency", "PostgreSQL")]
public sealed class CreditCardPdfConfirmationRepositoryTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    [Fact]
    public async Task ConfirmsMixedReconciledDocumentAtomicallyAndReplaysIdempotently()
    {
        var context = await ArrangeAsync("pdf-confirm-mixed");
        var confirmation = Confirmation(context, "doc-a", "decision-a", [
            Component("purchase", "Purchase", "Debit", "100.00", "Create", context.CategoryId),
            Component("fee", "Fee", "Debit", "10.00", "Create"),
            Component("credit", "Credit", "Credit", "-5.00", "Create"),
            Component("refund", "Refund", "Credit", "-2.00", "Create"),
            Component("payment", "Payment", "Credit", "-50.00", "Informational"),
        ], 103m);

        var first = await ConfirmAsync(context, confirmation);
        var replay = await ConfirmAsync(context, confirmation);

        Assert.Equal(CreditCardPdfConfirmationAccess.Allowed, first.Access);
        Assert.Equal(first.Result!.ImportId, replay.Result!.ImportId);
        Assert.Equal((1, 3, 1, "0.00"), (first.Result.CreatedPurchases, first.Result.CreatedAdjustments, first.Result.Informational, first.Result.FinalReconciliation.Difference));
        await using var db = Db();
        Assert.Single(db.FinancialCreditCardPurchases);
        Assert.Equal(3, await db.FinancialCreditCardInvoiceAdjustments.CountAsync());
        Assert.Empty(db.FinancialCreditCardInvoicePayments);
        Assert.Empty(db.FinancialTransactions);
        Assert.Single(db.FinancialCreditCardImportBatches.Where(item => item.Source == FinancialCreditCardImportSource.Pdf));
        Assert.Equal(5, await db.FinancialCreditCardImportItems.CountAsync());
        var batch = await db.FinancialCreditCardImportBatches.SingleAsync();
        Assert.DoesNotContain("ephemeral", JsonSerializer.Serialize(batch));
    }

    [Fact]
    public async Task DifferentDecisionsConflictAndChangedFinancialIdentityRollsBackWholeBatch()
    {
        var context = await ArrangeAsync("pdf-confirm-conflict");
        var valid = Confirmation(context, "doc-b", "decision-a", [Component("fee", "Fee", "Debit", "1.00", "Create")], 1m);
        Assert.Equal(CreditCardPdfConfirmationAccess.Allowed, (await ConfirmAsync(context, valid)).Access);
        Assert.Equal(CreditCardPdfConfirmationAccess.Conflict, (await ConfirmAsync(context, valid with { DecisionHash = "decision-b" })).Access);

        var changed = Confirmation(context, "doc-c", "decision-c", [
            Component("new-fee", "Fee", "Debit", "2.00", "Create"),
            Component("fee", "Fee", "Debit", "1.00", "Create"),
        ], 4m);
        Assert.Equal(CreditCardPdfConfirmationAccess.Conflict, (await ConfirmAsync(context, changed)).Access);
        await using var db = Db();
        Assert.DoesNotContain(await db.FinancialCreditCardInvoiceAdjustments.ToListAsync(), item => item.ExternalId.EndsWith("new-fee"));
        Assert.DoesNotContain(await db.FinancialCreditCardImportBatches.ToListAsync(), item => item.DocumentHash == "doc-c");
    }

    [Fact]
    public async Task RejectsForeignCategoryAndFamilyWithoutPartialWrites()
    {
        var context = await ArrangeAsync("pdf-confirm-isolation");
        var invalid = Confirmation(context, "doc-d", "decision-d", [
            Component("fee", "Fee", "Debit", "1.00", "Create"),
            Component("purchase", "Purchase", "Debit", "10.00", "Create", Guid.NewGuid()),
        ], 11m);
        Assert.Equal(CreditCardPdfConfirmationAccess.Invalid, (await ConfirmAsync(context, invalid)).Access);
        await using var foreignDb = Db();
        var foreign = await new CreditCardPdfConfirmationRepository(foreignDb).ConfirmAsync(Guid.NewGuid(), context.CardId, context.UserId, invalid, DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Equal(CreditCardPdfConfirmationAccess.NotFound, foreign.Access);
        await using var db = Db();
        Assert.Empty(db.FinancialCreditCardPurchases);
        Assert.Empty(db.FinancialCreditCardInvoiceAdjustments);
        Assert.Empty(db.FinancialCreditCardImportBatches);
    }

    [Fact]
    public async Task AlreadyExistingAdjustmentIsLinkedWithoutDuplication()
    {
        var context = await ArrangeAsync("pdf-confirm-existing");
        var component = Component("existing", "Fee", "Debit", "5.00", "AlreadyExists");
        await using (var db = Db())
        {
            var adjustment = FinancialCreditCardInvoiceAdjustment.Create(context.WorkspaceId, context.CardId, context.InvoiceId,
                FinancialCreditCardInvoiceAdjustmentType.Fee, FinancialCreditCardInvoiceAdjustmentNature.Debit,
                new(2026, 9, 1), "Fee", 5m, component.ComponentId, null, context.UserId, DateTimeOffset.UtcNow);
            db.Add(adjustment); await db.SaveChangesAsync();
            component = component with { ExistingEntityId = adjustment.Id };
        }
        var result = await ConfirmAsync(context, Confirmation(context, "doc-existing", "decision-existing", [component], 5m));
        Assert.Equal(CreditCardPdfConfirmationAccess.Allowed, result.Access);
        Assert.Equal(1, result.Result!.AlreadyExisting);
        await using var verify = Db();
        Assert.Equal(1, await verify.FinancialCreditCardInvoiceAdjustments.CountAsync());
    }

    private async Task<TestContext> ArrangeAsync(string subject)
    {
        var session = await SignInAsync(subject);
        var me = await GetMeAsync(session);
        var family = await CreateWorkspaceAsync(session, "Família PDF confirm");
        await using var identityDb = Db();
        var userId = await identityDb.Users.Where(user => user.Issuer == me.Issuer && user.Subject == me.Subject).Select(user => user.Id).SingleAsync();
        var categoryResponse = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{family.Id}/categories", session, new { name = "Compras", type = "Expense" });
        var categoryId = (await ReadAsync<JsonElement>(categoryResponse)).GetProperty("id").GetGuid();
        var cardResponse = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{family.Id}/credit-cards", session, new { name = "Cartão", issuer = "Itau", holderUserId = userId, creditLimit = "1000.00", closingDay = 10, dueDay = 20, defaultPaymentAccountId = (Guid?)null });
        var cardId = (await ReadAsync<JsonElement>(cardResponse)).GetProperty("id").GetGuid();
        await using var db = Db();
        var invoice = FinancialCreditCardInvoice.Create(family.Id, cardId, "2026-09", new(2026, 8, 11), new(2026, 9, 10), new(2026, 9, 20), DateTimeOffset.UtcNow);
        db.Add(invoice); await db.SaveChangesAsync();
        return new(family.Id, cardId, invoice.Id, userId, categoryId);
    }

    private async Task<CreditCardPdfConfirmationOperationResult> ConfirmAsync(TestContext context, CreditCardPdfTrustedConfirmation confirmation)
    {
        await using var db = Db();
        return await new CreditCardPdfConfirmationRepository(db).ConfirmAsync(context.WorkspaceId, context.CardId, context.UserId, confirmation, DateTimeOffset.UtcNow, CancellationToken.None);
    }

    private static CreditCardPdfTrustedConfirmation Confirmation(TestContext context, string documentHash, string decisionHash, IReadOnlyList<CreditCardPdfTrustedComponent> items, decimal official) =>
        new(documentHash, decisionHash, "ItauMastercard", "2026-09", official, context.InvoiceId, items);
    private static CreditCardPdfTrustedComponent Component(string id, string type, string nature, string amount, string classification, Guid? category = null) =>
        new($"pdf:hash:p1:r{id}", Math.Abs(id.GetHashCode()), type, nature, new(2026, 9, 1), type, decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), classification, null, category);
    private sealed record TestContext(Guid WorkspaceId, Guid CardId, Guid InvoiceId, Guid UserId, Guid CategoryId);
}
