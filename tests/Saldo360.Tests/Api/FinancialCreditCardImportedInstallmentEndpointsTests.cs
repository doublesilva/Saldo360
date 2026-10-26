using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.CreditCards;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

[Collection(PostgresSecondaryCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Cards")]
[Trait("Dependency", "PostgreSQL")]
public sealed class FinancialCreditCardImportedInstallmentEndpointsTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    [Fact]
    public async Task MatchesAndLinksExistingInstallmentIdempotently()
    {
        var owner = await SignInAsync("historical-link-owner");
        var family = await CreateWorkspaceAsync(owner);
        var category = await CreateCategoryAsync(family.Id, owner);
        var card = await CreateCardAsync(family.Id, owner);
        var purchase = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{family.Id}/credit-cards/{card}/purchases", owner,
            new { description = "Compra parcelada", purchaseDate = new DateOnly(2026, 9, 1), totalAmount = "600.00", installmentCount = 6, categoryId = category });
        Assert.Equal(HttpStatusCode.Created, purchase.StatusCode);
        (Guid Id, Guid? InvoiceId, decimal Amount) installment = default;
        await WithDbAsync(async db => { var row = await db.FinancialCreditCardInstallments.Where(i => i.WorkspaceId == family.Id && i.InstallmentNumber == 2).Select(i => new { i.Id, i.InvoiceId, i.Amount }).SingleAsync(); installment = (row.Id, row.InvoiceId, row.Amount); });
        var body = new { importId = Guid.NewGuid(), rowNumber = 2, installmentId = installment.Id, transactionDate = "2026-09-01", description = "Compra parcelada", amount = "100.00", installmentNumber = 2, installmentCount = 6, competence = (string?)null, externalId = "linha-2", confirm = true };

        var matches = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{family.Id}/credit-cards/{card}/imports/installments/matches", owner, body);
        Assert.Equal(HttpStatusCode.OK, matches.StatusCode);
        Assert.True((await ReadAsync<JsonElement>(matches)).GetArrayLength() > 0);
        var linked = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{family.Id}/credit-cards/{card}/imports/installments/link", owner, body);
        Assert.Equal(HttpStatusCode.OK, linked.StatusCode);
        var linkedId = (await ReadAsync<JsonElement>(linked)).GetProperty("id").GetGuid();
        var retry = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{family.Id}/credit-cards/{card}/imports/installments/link", owner, body);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(linkedId, (await ReadAsync<JsonElement>(retry)).GetProperty("id").GetGuid());
        await WithDbAsync(async db => Assert.Single(db.FinancialCreditCardImportedInstallments));
    }

    [Fact]
    public async Task CreatesAndCancelsHistoricalObligationWithoutPurchase()
    {
        var owner = await SignInAsync("historical-create-owner");
        var family = await CreateWorkspaceAsync(owner);
        var category = await CreateCategoryAsync(family.Id, owner);
        var card = await CreateCardAsync(family.Id, owner);
        var purchase = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{family.Id}/credit-cards/{card}/purchases", owner,
            new { description = "Compra base", purchaseDate = new DateOnly(2026, 9, 1), totalAmount = "100.00", installmentCount = 1, categoryId = category });
        Assert.Equal(HttpStatusCode.Created, purchase.StatusCode);
        var body = new { importId = Guid.NewGuid(), rowNumber = 4, transactionDate = "2026-09-01", description = "Parcela histórica", amount = "150.25", installmentNumber = 2, installmentCount = 6, competence = "2026-09", confirm = true };
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{family.Id}/credit-cards/{card}/imports/installments/historical", owner, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var record = await ReadAsync<JsonElement>(response);
        Assert.True(record.GetProperty("isHistorical").GetBoolean());
        Assert.Equal("150.25", record.GetProperty("amount").GetString());
        await WithDbAsync(async db =>
        {
            Assert.Single(db.FinancialCreditCardImportedInstallments);
            Assert.Single(db.FinancialCreditCardPurchases);
        });
        var cancelled = await SendWithCsrfAsync(HttpMethod.Delete, $"/api/financial-workspaces/{family.Id}/credit-cards/{card}/imports/installments/{record.GetProperty("id").GetGuid()}", owner);
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
    }

    [Fact]
    public async Task RejectsUnconfirmedOrMissingCompetence()
    {
        var owner = await SignInAsync("historical-validation-owner");
        var family = await CreateWorkspaceAsync(owner);
        var card = await CreateCardAsync(family.Id, owner);
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{family.Id}/credit-cards/{card}/imports/installments/historical", owner,
            new { importId = Guid.NewGuid(), rowNumber = 2, transactionDate = "2026-09-01", description = "Ambígua", amount = "1.00", installmentNumber = 2, installmentCount = 6, confirm = false });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await WithDbAsync(async db => Assert.Empty(db.FinancialCreditCardImportedInstallments));
    }

    private async Task<Guid> CreateCardAsync(Guid workspaceId, string owner)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{workspaceId}/credit-cards", owner,
            new { name = "Cartão histórico", issuer = "Banco", creditLimit = "5000.00", closingDay = 10, dueDay = 20, defaultPaymentAccountId = (Guid?)null });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<JsonElement>(response)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateCategoryAsync(Guid workspaceId, string owner)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{workspaceId}/categories", owner, new { name = "Compras", type = "Expense" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<JsonElement>(response)).GetProperty("id").GetGuid();
    }
}
