using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.CreditCards;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

[Collection(PostgresCollection.Name)]
public sealed class CardConsolidationTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    [Fact]
    public async Task LinkedImportedRowsAndPaymentsAreNotCountedTwiceAndArchivedDebtRemainsVisible()
    {
        var s = await SignInAsync("paid-consolidation"); var w = await CreateWorkspaceAsync(s);
        var root = $"/api/financial-workspaces/{w.Id}";
        async Task<Guid> Create(string path, object input) { var r = await SendWithCsrfAsync(HttpMethod.Post, root + path, s, input); Assert.Equal(HttpStatusCode.Created, r.StatusCode); return (await ReadAsync<JsonElement>(r)).GetProperty("id").GetGuid(); }
        var account = await Create("/accounts", new { name = "Synthetic", type = "Checking", financialScope = "PF", openingBalance = "1000.00", openingBalanceDate = "2019-01-01" });
        var category = await Create("/categories", new { name = "Synthetic", type = "Expense" });
        var card = await Create("/credit-cards", new { name = "Synthetic", issuer = "Bank", creditLimit = "10000.00", closingDay = 5, dueDay = 10, defaultPaymentAccountId = account });
        await Create($"/credit-cards/{card}/purchases", new { description = "Synthetic", purchaseDate = "2020-01-01", totalAmount = "100.00", installmentCount = 1, categoryId = category });
        Guid invoice;
        await using (var db = Db())
        {
            var inv = await db.FinancialCreditCardInvoices.SingleAsync(); invoice = inv.Id;
            var known = FinancialCreditCardImportedInstallment.CreateKnown(w.Id, card, category, "Linked synthetic", 20m, 1, 1, new(2020, 1, 1), inv.DueDate, "Synthetic", "linked", w.OwnerUserId, DateTimeOffset.UtcNow);
            known.AttachKnownInvoice(inv); db.Add(known);
            // 89 synthetic, explicitly known rows: never private source data or an importer execution.
            for (var i = 0; i < 89; i++) db.Add(FinancialCreditCardImportedInstallment.CreateKnown(w.Id, card, category, "Known synthetic", 1m, null, null, new(2030, 11, 1), new(2030, 11, 10), "Synthetic", $"synthetic:{i}", w.OwnerUserId, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
            await OfficialInvoiceFixture.CloseAsync(db, invoice);
        }
        async Task<JsonElement> Row(string competence) { var r = await SendAsync(HttpMethod.Get, root + $"/credit-cards/summary?competence={competence}", s); Assert.Equal(HttpStatusCode.OK, r.StatusCode); return (await ReadAsync<JsonElement>(r)).GetProperty("cards")[0]; }
        var before = await Row("2020-01");
        Assert.Equal("120.00", before.GetProperty("knownPeriodTotal").GetString());
        Assert.Equal("0.00", before.GetProperty("unlinkedPeriodAmount").GetString());
        await Create($"/credit-cards/{card}/invoices/{invoice}/payments", new { operationId = Guid.NewGuid(), financialAccountId = account, paymentDate = "2026-10-23", amount = "120.00" });
        var paid = await Row("2020-01");
        Assert.Equal("120.00", paid.GetProperty("knownPeriodTotal").GetString());
        Assert.Equal("120.00", paid.GetProperty("paidPeriodAmount").GetString());
        Assert.Equal("0.00", paid.GetProperty("outstandingPeriodAmount").GetString());
        Assert.Equal("89.00", paid.GetProperty("usedLimit").GetString());
        await using var verify = Db();
        var snapshot = JsonSerializer.Serialize(await verify.FinancialCreditCardImportedInstallments.AsNoTracking().OrderBy(i => i.Id).ToListAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await SendWithCsrfAsync(HttpMethod.Delete, root + $"/credit-cards/{card}", s)).StatusCode);
        var archived = await Row("2030-11");
        Assert.Equal(89, archived.GetProperty("unlinkedObligations").GetArrayLength());
        Assert.Equal("89.00", archived.GetProperty("knownPeriodTotal").GetString());
        var projection = await SendAsync(HttpMethod.Get, root + $"/accounts/{account}/projection?through=2030-11-30", s);
        Assert.Equal("89.00", (await ReadAsync<JsonElement>(projection)).GetProperty("creditCardOutstanding").GetString());
        Assert.Equal(snapshot, JsonSerializer.Serialize(await verify.FinancialCreditCardImportedInstallments.AsNoTracking().OrderBy(i => i.Id).ToListAsync()));
        Assert.Single(await verify.FinancialCreditCardInvoicePayments.ToListAsync());
    }
    [Theory]
    [InlineData(true, false, "100.00", "0.00", "100.00")]
    [InlineData(false, true, "0.00", "20.00", "20.00")]
    [InlineData(true, true, "100.00", "20.00", "120.00")]
    [InlineData(false, false, "0.00", "0.00", "0.00")]
    public async Task ComponentsAreDisjointAndReadsPreserveSyntheticHistory(bool purchase, bool imported, string invoiceExpected, string importedExpected, string totalExpected)
    {
        var session = await SignInAsync("consolidation"); var w = await CreateWorkspaceAsync(session);
        var cat = await Create("categories", new { name = "Synthetic", type = "Expense" });
        var card = await Create("credit-cards", new { name = "Synthetic", issuer = "Bank", creditLimit = "10000.00", closingDay = 5, dueDay = 10 });
        var otherCard = await Create("credit-cards", new { name = "Other", issuer = "Bank", creditLimit = "10000.00", closingDay = 5, dueDay = 10 });
        if (purchase) Assert.Equal(HttpStatusCode.Created, (await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{w.Id}/credit-cards/{card}/purchases", session, new { description = "Synthetic purchase", purchaseDate = "2030-10-10", totalAmount = "600.00", installmentCount = 6, categoryId = cat })).StatusCode);
        await using (var db = Db())
        {
            if (imported)
                for (var month = 0; month < 6; month++)
                {
                    var c = new DateOnly(2030, 11, 1).AddMonths(month);
                    db.FinancialCreditCardImportedInstallments.Add(FinancialCreditCardImportedInstallment.CreateKnown(w.Id, card, cat, "Known", 20m, null, null, c, null, "Synthetic", $"known:{month}", w.OwnerUserId, DateTimeOffset.UtcNow));
                }
            var cancelled = FinancialCreditCardImportedInstallment.CreateKnown(w.Id, card, cat, "Cancelled", 999m, 1, 1, new(2030, 11, 1), new(2030, 11, 10), "Synthetic", "cancelled", w.OwnerUserId, DateTimeOffset.UtcNow);
            cancelled.Cancel(w.OwnerUserId, DateTimeOffset.UtcNow); db.Add(cancelled);
            db.Add(FinancialCreditCardImportedInstallment.CreateKnown(w.Id, otherCard, cat, "Other", 123m, 1, 1, new(2030, 11, 1), null, "Synthetic", "other", w.OwnerUserId, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }
        var before = await Snapshot();
        for (var month = 0; month < 6; month++)
        {
            var c = new DateOnly(2030, 11, 1).AddMonths(month).ToString("yyyy-MM");
            var summary = await Summary(c); var row = summary.GetProperty("cards").EnumerateArray().Single(i => i.GetProperty("cardId").GetGuid() == card);
            Assert.Equal(invoiceExpected, row.GetProperty("invoiceTotal").GetString());
            Assert.Equal(importedExpected, row.GetProperty("unlinkedPeriodAmount").GetString());
            Assert.Equal(totalExpected, row.GetProperty("knownPeriodTotal").GetString());
            Assert.Equal(totalExpected, row.GetProperty("outstandingPeriodAmount").GetString());
            Assert.Equal(imported ? 1 : 0, row.GetProperty("incompleteDetailsCount").GetInt32());
            Assert.Equal(imported ? 1 : 0, row.GetProperty("unlinkedObligations").GetArrayLength());
            Assert.Equal((purchase ? 600m : 0m) + (imported ? 120m : 0m), decimal.Parse(row.GetProperty("usedLimit").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        }
        Assert.Equal(before, await Snapshot());
        Assert.Equal(HttpStatusCode.NoContent, (await SendWithCsrfAsync(HttpMethod.Delete, $"/api/financial-workspaces/{w.Id}/credit-cards/{card}", session)).StatusCode);
        var archived = await Summary("2030-11");
        Assert.Equal(1, archived.GetProperty("cardCount").GetInt32());
        var archivedRow = archived.GetProperty("cards").EnumerateArray().Single(i => i.GetProperty("cardId").GetGuid() == card);
        Assert.True(archivedRow.GetProperty("isArchived").GetBoolean());
        Assert.Equal(totalExpected, archivedRow.GetProperty("knownPeriodTotal").GetString());
        Assert.Equal(before, await Snapshot());
        var stranger = await SignInAsync("stranger"); var otherWorkspace = await CreateWorkspaceAsync(stranger);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, $"/api/financial-workspaces/{w.Id}/credit-cards/summary?competence=2030-11", stranger)).StatusCode);
        var own = await ReadAsync<JsonElement>(await SendAsync(HttpMethod.Get, $"/api/financial-workspaces/{otherWorkspace.Id}/credit-cards/summary?competence=2030-11", stranger));
        Assert.Empty(own.GetProperty("cards").EnumerateArray());

        async Task<Guid> Create(string resource, object payload) { var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{w.Id}/{resource}", session, payload); Assert.Equal(HttpStatusCode.Created, response.StatusCode); return (await ReadAsync<JsonElement>(response)).GetProperty("id").GetGuid(); }
        async Task<JsonElement> Summary(string competence) { var response = await SendAsync(HttpMethod.Get, $"/api/financial-workspaces/{w.Id}/credit-cards/summary?competence={competence}", session); Assert.Equal(HttpStatusCode.OK, response.StatusCode); return await ReadAsync<JsonElement>(response); }
        async Task<string> Snapshot() { await using var db = Db(); return JsonSerializer.Serialize(new { imports = await db.FinancialCreditCardImportedInstallments.AsNoTracking().OrderBy(i => i.Id).ToListAsync(), purchases = await db.FinancialCreditCardPurchases.AsNoTracking().Include(i => i.Installments).OrderBy(i => i.Id).ToListAsync(), payments = await db.FinancialCreditCardInvoicePayments.AsNoTracking().OrderBy(i => i.Id).ToListAsync() }); }
    }
}
