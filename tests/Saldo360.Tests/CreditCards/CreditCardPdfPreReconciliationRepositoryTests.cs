using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.CreditCards;
using Saldo360.Infrastructure.CreditCards;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.CreditCards;

[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "CORE")]
public sealed class CreditCardPdfPreReconciliationRepositoryTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    [Fact]
    public async Task ReadsExistingImportIdentityInOneFinancialSnapshotWithoutWriting()
    {
        var session = await SignInAsync("pdf-reconciliation-owner");
        var me = await GetMeAsync(session);
        var family = await CreateWorkspaceAsync(session, "Família PDF");
        await using var identityDb = Db();
        var userId = await identityDb.Users.Where(user => user.Issuer == me.Issuer && user.Subject == me.Subject).Select(user => user.Id).SingleAsync();
        var created = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{family.Id}/credit-cards", session,
            new { name = "Cartão PDF", issuer = "Itau", holderUserId = userId, creditLimit = "1000.00", closingDay = 8, dueDay = 15, defaultPaymentAccountId = (Guid?)null });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var cardId = (await ReadAsync<JsonElement>(created)).GetProperty("id").GetGuid();
        var category = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{family.Id}/categories", session,
            new { name = "Compras", type = "Expense" });
        Assert.Equal(HttpStatusCode.Created, category.StatusCode);
        var categoryId = (await ReadAsync<JsonElement>(category)).GetProperty("id").GetGuid();

        await using var db = Db();
        var now = DateTimeOffset.Parse("2026-10-01T12:00:00Z");
        var invoice = FinancialCreditCardInvoice.Create(family.Id, cardId, "2026-10", new(2026, 9, 9), new(2026, 10, 8), new(2026, 10, 15), now);
        var purchase = FinancialCreditCardPurchase.Create(family.Id, cardId, categoryId, "LOJA", new(2026, 10, 2), 40m, 1, userId, now);
        purchase.Installments.Single().AssignInvoice(invoice.Id);
        var importId = Guid.NewGuid();
        var batch = FinancialCreditCardImportBatch.Create(importId, family.Id, cardId, "hash", userId, now);
        batch.AddItem(FinancialCreditCardImportItem.Create(family.Id, cardId, importId, 10, "pdf:hash:p2:r10", purchase.Id, now));
        db.AddRange(invoice, purchase, batch);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var beforePurchases = await db.FinancialCreditCardPurchases.CountAsync();
        var beforeTransactions = await db.FinancialTransactions.CountAsync();

        var repository = new CreditCardPdfPreReconciliationRepository(db);
        var snapshot = await repository.GetSnapshotAsync(family.Id, cardId, userId, "2026-10", CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal("Validated", snapshot!.Invoice is null ? "Missing" : "Validated");
        var operation = Assert.Single(snapshot.Operations);
        Assert.Equal("pdf:hash:p2:r10", operation.ExternalId);
        Assert.Equal("Installment", operation.EntityType);
        Assert.Equal(beforePurchases, await db.FinancialCreditCardPurchases.CountAsync());
        Assert.Equal(beforeTransactions, await db.FinancialTransactions.CountAsync());
        Assert.False(db.ChangeTracker.HasChanges());
        Assert.Null(await repository.GetSnapshotAsync(family.Id, cardId, Guid.NewGuid(), "2026-10", CancellationToken.None));
    }
}
