using System.Net;
using System.Text.Json;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

[Collection(PostgresCollection.Name)]
public sealed class FinancialCreditCardImportEndpointsTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    [Fact]
    public async Task ConfirmCreatesPurchaseInvoicesAndIsIdempotent()
    {
        var owner = await SignInAsync("import-confirm-owner");
        var family = await CreateFamilyAsync(owner, "Família importação");
        var categoryResponse = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{family.Id}/categories", owner, new { name = "Compras", type = "Expense" });
        var category = (await ReadAsync<JsonElement>(categoryResponse)).GetProperty("id").GetGuid();
        var cardResponse = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{family.Id}/credit-cards", owner,
            new { name = "Cartão", issuer = "Banco", creditLimit = "5000.00", closingDay = 10, dueDay = 20, defaultPaymentAccountId = (Guid?)null });
        var card = (await ReadAsync<JsonElement>(cardResponse)).GetProperty("id").GetGuid();
        var importId = Guid.NewGuid();
        var body = new { importId, confirm = true, items = new[] { new { rowNumber = 2, transactionDate = "2026-10-05", description = "Compra importada", amount = "123.45", categoryId = category, installmentNumber = 1, installmentCount = 1, totalAmount = "123.45", externalId = "statement-1", competence = (string?)null } } };

        var first = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{family.Id}/credit-cards/{card}/imports/confirm", owner, body);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstJson = await ReadAsync<JsonElement>(first);
        Assert.Equal(1, firstJson.GetProperty("importedCount").GetInt32());
        var purchaseId = firstJson.GetProperty("items")[0].GetProperty("purchaseId").GetGuid();

        var retry = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{family.Id}/credit-cards/{card}/imports/confirm", owner, body);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(purchaseId, (await ReadAsync<JsonElement>(retry)).GetProperty("items")[0].GetProperty("purchaseId").GetGuid());

        var changed = new { importId, confirm = true, items = new[] { new { rowNumber = 2, transactionDate = "2026-10-05", description = "Compra importada", amount = "123.46", categoryId = category, installmentNumber = 1, installmentCount = 1, totalAmount = "123.46", externalId = "statement-1", competence = (string?)null } } };
        Assert.Equal(HttpStatusCode.Conflict, (await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{family.Id}/credit-cards/{card}/imports/confirm", owner, changed)).StatusCode);
        await WithDbAsync(async db => Assert.Single(db.FinancialCreditCardPurchases));
    }

    [Fact]
    public async Task RejectsIntermediateInstallmentAtomically()
    {
        var owner = await SignInAsync("import-installment-owner");
        var family = await CreateFamilyAsync(owner, "Família parcela");
        var categoryResponse = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{family.Id}/categories", owner, new { name = "Compras", type = "Expense" });
        var category = (await ReadAsync<JsonElement>(categoryResponse)).GetProperty("id").GetGuid();
        var cardResponse = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{family.Id}/credit-cards", owner,
            new { name = "Cartão", issuer = "Banco", creditLimit = "5000.00", closingDay = 10, dueDay = 20, defaultPaymentAccountId = (Guid?)null });
        var card = (await ReadAsync<JsonElement>(cardResponse)).GetProperty("id").GetGuid();
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{family.Id}/credit-cards/{card}/imports/confirm", owner,
            new { importId = Guid.NewGuid(), confirm = true, items = new[] { new { rowNumber = 2, transactionDate = "2026-10-05", description = "Parcela 2/6", amount = "150.00", categoryId = category, installmentNumber = 2, installmentCount = 6, totalAmount = (string?)null, externalId = (string?)null, competence = (string?)null } } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await WithDbAsync(async db => Assert.Empty(db.FinancialCreditCardPurchases));
    }
}
