using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Saldo360.Domain.CreditCards;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

[Collection(PostgresCollection.Name)]
public sealed class OngoingCreditCardPurchaseTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private string owner = null!;
    private Guid workspace;
    private Guid category;
    private Guid account;
    private Guid card;
    private string Root => $"/api/financial-workspaces/{workspace}/credit-cards/{card}";
    private object Input(string amount = "1000.00", int count = 4, int paid = 2, string? first = "2030-11-01", string mode = "Total", string date = "2026-03-26", string description = "Compra sintética") =>
        new { description, purchaseDate = date, totalAmount = amount, installmentCount = count, categoryId = category, amountMode = mode, previouslyPaidInstallments = paid, firstPendingCompetence = first };

    [Fact]
    public async Task HistoricalScheduleCreatesOnlyPendingObligationsAndNoBankPayment()
    {
        await ArrangeAsync();
        var preview = await PostAsync("/purchases/preview", Input());
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var summary = await ReadAsync<JsonElement>(preview);
        Assert.Equal("500.00", summary.GetProperty("remainingAmount").GetString());
        Assert.Equal(new[] { 3, 4 }, summary.GetProperty("installments").EnumerateArray().Select(i => i.GetProperty("number").GetInt32()));
        await using (var db = Db()) Assert.Empty(await db.FinancialCreditCardPurchases.ToListAsync());

        var created = await PostAsync("/purchases", Input());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var purchase = await ReadAsync<JsonElement>(created);
        Assert.Equal("1000.00", purchase.GetProperty("totalAmount").GetString());
        Assert.Equal(2, purchase.GetProperty("previouslyPaidInstallments").GetInt32());
        Assert.Equal(2, purchase.GetProperty("installments").GetArrayLength());
        var invoices = await GetAsync("/invoices?from=2030-01&through=2031-01");
        Assert.Equal(new[] { "2030-11", "2030-12" }, invoices.EnumerateArray().Select(i => i.GetProperty("competence").GetString()));
        Assert.All(invoices.EnumerateArray(), i => Assert.Equal("250.00", i.GetProperty("totalAmount").GetString()));
        var limit = await GetAsync("/limit");
        Assert.Equal("500.00", limit.GetProperty("usedLimit").GetString());
        Assert.Equal("9500.00", limit.GetProperty("availableLimit").GetString());
        var projection = await SendAsync(HttpMethod.Get, $"/api/financial-workspaces/{workspace}/accounts/{account}/projection?through=2030-12-31", owner);
        Assert.Equal("500.00", (await ReadAsync<JsonElement>(projection)).GetProperty("creditCardOutstanding").GetString());
        await using var verify = Db();
        Assert.Empty(await verify.FinancialTransactions.ToListAsync());
        Assert.Empty(await verify.FinancialCreditCardInvoicePayments.ToListAsync());
        Assert.Empty(await verify.FinancialTransfers.ToListAsync());
    }

    [Theory]
    [InlineData("100.00", 3, "Total", "100.00", "33.34", "33.33")]
    [InlineData("320.71", 10, "Installment", "3207.10", "320.71", "320.71")]
    [InlineData("39.90", 1, "Total", "39.90", "39.90", "39.90")]
    public async Task NewPurchasesUseExactServerPreview(string amount, int count, string mode, string total, string first, string last)
    {
        await ArrangeAsync();
        var response = await PostAsync("/purchases/preview", Input(amount, count, 0, "2030-11-01", mode));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadAsync<JsonElement>(response);
        Assert.Equal(total, json.GetProperty("totalAmount").GetString());
        var rows = json.GetProperty("installments");
        Assert.Equal(first, rows[0].GetProperty("amount").GetString());
        Assert.Equal(last, rows[count - 1].GetProperty("amount").GetString());
        Assert.Equal(decimal.Parse(total, System.Globalization.CultureInfo.InvariantCulture), rows.EnumerateArray().Sum(i => decimal.Parse(i.GetProperty("amount").GetString()!, System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(HttpStatusCode.Created, (await PostAsync("/purchases", Input(amount, count, 0, "2030-11-01", mode))).StatusCode);
    }

    [Fact]
    public async Task EditPreservesPurchaseAndInstallmentIdentitiesAndMovesFutureInvoicesWithoutDuplicates()
    {
        await ArrangeAsync();
        var original = await ReadAsync<JsonElement>(await PostAsync("/purchases", Input("100.00", 3, 0)));
        var id = original.GetProperty("id").GetGuid();
        var ids = original.GetProperty("installments").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToArray();
        var change = Input("120.00", 2, 0, "2030-12-01");
        for (var replay = 0; replay < 2; replay++)
        {
            var update = await SendWithCsrfAsync(HttpMethod.Put, Root + $"/purchases/{id}", owner, change);
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
            Assert.Equal(id, (await ReadAsync<JsonElement>(update)).GetProperty("id").GetGuid());
        }
        await using (var db = Db())
        {
            var p = await db.FinancialCreditCardPurchases.Include(p => p.Installments).SingleAsync();
            Assert.Equal(3, p.Installments.Count);
            Assert.Equal(2, p.Installments.Count(i => i.Status == CreditCardInstallmentStatus.Active));
            Assert.Equal(ids.Order(), p.Installments.Select(i => i.Id).Order());
        }
        var projection = await GetAsync("/invoice-projection?from=2030-11&months=6");
        Assert.Equal("0.00", projection[0].GetProperty("totalAmount").GetString());
        Assert.Equal("60.00", projection[1].GetProperty("totalAmount").GetString());
        Assert.Equal("60.00", projection[2].GetProperty("totalAmount").GetString());
        Assert.Equal("120.00", (await GetAsync("/limit")).GetProperty("usedLimit").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync($"/purchases/{id}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync($"/purchases/{id}/cancel", null)).StatusCode);
        Assert.Equal("0.00", (await GetAsync("/limit")).GetProperty("usedLimit").GetString());
    }

    [Fact]
    public async Task PaidHistoryAndClosedInvoicesRejectEditingWithoutPartialWrites()
    {
        await ArrangeAsync();
        var p = await ReadAsync<JsonElement>(await PostAsync("/purchases", Input()));
        var id = p.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await SendWithCsrfAsync(HttpMethod.Put, Root + $"/purchases/{id}", owner, Input("2000.00"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendWithCsrfAsync(HttpMethod.Put, Root + $"/purchases/{id}", owner, Input(description: "Descrição corrigida"))).StatusCode);
        // Existing past purchases remain valid. They cannot be silently rewritten once consolidated.
        var past = await ReadAsync<JsonElement>(await PostAsync("/purchases", Input("50.00", 1, 0, null, date: "2020-01-01")));
        Assert.Equal(HttpStatusCode.Conflict, (await SendWithCsrfAsync(HttpMethod.Put, Root + $"/purchases/{past.GetProperty("id").GetGuid()}", owner, Input("60.00", 1, 0, null, date: "2020-01-01"))).StatusCode);
        // An attempted move to a closed destination must not persist the description or schedule.
        Assert.Equal(HttpStatusCode.Conflict, (await SendWithCsrfAsync(HttpMethod.Put, Root + $"/purchases/{id}", owner, Input(first: "2020-01-01", date: "2019-01-01"))).StatusCode);
        await using var db = Db();
        var stored = await db.FinancialCreditCardPurchases.FindAsync(id);
        Assert.Equal(1000m, stored!.TotalAmount);
        Assert.Equal("Descrição corrigida", stored.Description);
        Assert.Equal(new DateOnly(2030, 11, 1), stored.FirstPendingCompetence);
    }

    [Fact]
    public async Task InvalidInputsIsolationCsrfAndPortfolioRemainScoped()
    {
        await ArrangeAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/purchases/preview", Input(paid: 4))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/purchases", Input(first: null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/purchases", Input("1.001"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/purchases", Input("-79228162514264337593543950335"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/purchases", Input(mode: "Unknown"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/purchases", Input(first: "2030-11-02"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/purchases", Input(first: "2020-01-01"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/purchases", Input(first: "2030-11-01", date: "2030-11-10"))).StatusCode);
        var stranger = await SignInAsync("other-purchase-owner");
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Post, Root + "/purchases/preview", stranger, Input())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Post, Root + "/purchases", stranger, Input())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Put, Root + $"/purchases/{Guid.NewGuid()}", stranger, Input())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendJsonAsync(HttpMethod.Post, Root + "/purchases/preview", owner, null, Input())).StatusCode);
        await PostAsync("/purchases", Input());
        var summaryUrl = $"/api/financial-workspaces/{workspace}/credit-cards/summary?competence=2030-11";
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, summaryUrl, stranger)).StatusCode);
        var summary = await ReadAsync<JsonElement>(await SendAsync(HttpMethod.Get, summaryUrl, owner));
        Assert.Equal(1, summary.GetProperty("cardCount").GetInt32());
        Assert.Equal("10000.00", summary.GetProperty("totalLimit").GetString());
        Assert.Equal("250.00", summary.GetProperty("invoiceTotal").GetString());
        Assert.Equal("9500.00", summary.GetProperty("availableLimit").GetString());
        await using var db = Db();
        Assert.Single(await db.FinancialCreditCardPurchases.ToListAsync());
    }

    [Fact]
    public async Task DowngradeRefusesLossOfExplicitHistory()
    {
        await ArrangeAsync();
        await PostAsync("/purchases", Input());
        await using var db = Db();
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.GetService<IMigrator>().MigrateAsync("20261022165204_AddBudgetBootstrapExtensions"));
        db.ChangeTracker.Clear();
        Assert.Equal(2, (await db.FinancialCreditCardPurchases.SingleAsync()).PreviouslyPaidInstallments);
    }

    [Fact]
    public async Task KnownObligationExplainsUsedLimitWithoutInventingAnInvoice()
    {
        await ArrangeAsync();
        await using (var db = Db())
        {
            var userId = (await db.FinancialWorkspaces.SingleAsync()).OwnerUserId;
            db.FinancialCreditCardImportedInstallments.Add(FinancialCreditCardImportedInstallment.CreateKnown(workspace, card, category, "Parcela sintética", 320.71m, 7, 10, new DateOnly(2030, 11, 1), new DateOnly(2030, 11, 10), "Synthetic", "known:7", userId, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }
        var response = await SendAsync(HttpMethod.Get, $"/api/financial-workspaces/{workspace}/credit-cards/summary?competence=2030-11", owner);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await ReadAsync<JsonElement>(response);
        Assert.Equal("0.00", summary.GetProperty("invoiceTotal").GetString());
        Assert.Equal("9679.29", summary.GetProperty("availableLimit").GetString());
        Assert.Equal(1, summary.GetProperty("cards")[0].GetProperty("unlinkedKnownCount").GetInt32());
        Assert.Equal("320.71", summary.GetProperty("cards")[0].GetProperty("unlinkedKnownAmount").GetString());
        Assert.Equal("3.21", summary.GetProperty("cards")[0].GetProperty("utilizationPercentage").GetString());
        Assert.Empty((await GetAsync("/invoices?from=2030-11&through=2030-11")).EnumerateArray());
        await using var verify = Db();
        Assert.Empty(await verify.FinancialCreditCardPurchases.ToListAsync());
        Assert.Single(await verify.FinancialCreditCardImportedInstallments.ToListAsync());
    }

    [Fact]
    public async Task MovingToClosedDestinationRollsBackAllStagedChanges()
    {
        await ArrangeAsync();
        await PostAsync("/purchases", Input("50.00", 1, 0, null, date: "2020-01-01"));
        var created = await ReadAsync<JsonElement>(await PostAsync("/purchases", Input("100.00", 2, 0)));
        var id = created.GetProperty("id").GetGuid();
        var response = await SendWithCsrfAsync(HttpMethod.Put, Root + $"/purchases/{id}", owner, Input("120.00", 2, 0, "2020-01-01", date: "2019-01-01", description: "Não persistir"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await using var db = Db();
        var p = await db.FinancialCreditCardPurchases.Include(p => p.Installments).SingleAsync(p => p.Id == id);
        Assert.Equal(100m, p.TotalAmount);
        Assert.Equal("Compra sintética", p.Description);
        Assert.All(p.Installments, i => Assert.Equal(50m, i.Amount));
        Assert.Equal(3, await db.FinancialCreditCardInvoices.CountAsync());
    }

    [Fact]
    public async Task ExplicitCompetenceCannotSilentlyShiftWhenClosingAndDueDaysClampToSameDate()
    {
        await ArrangeAsync();
        await using (var db = Db())
        {
            var c = await db.FinancialCreditCards.SingleAsync();
            c.Update(new CreditCardDetails(c.Name, c.Issuer, c.LastFourDigits, c.HolderUserId, c.CreditLimit, 30, 31, c.DefaultPaymentAccountId), c.CreatedByUserId, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }
        var result = await PostAsync("/purchases/preview", Input("50.00", 1, 0, "2030-02-01"));
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/purchases", Input("50.00", 1, 0, "2030-02-01"))).StatusCode);
        await using var verify = Db();
        Assert.Empty(await verify.FinancialCreditCardPurchases.ToListAsync());
        Assert.Empty(await verify.FinancialCreditCardInvoices.ToListAsync());
    }

    private Task<HttpResponseMessage> PostAsync(string path, object? input) => SendWithCsrfAsync(HttpMethod.Post, Root + path, owner, input);
    private async Task<JsonElement> GetAsync(string path)
    {
        var r = await SendAsync(HttpMethod.Get, Root + path, owner);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return await ReadAsync<JsonElement>(r);
    }
    private async Task ArrangeAsync()
    {
        owner = await SignInAsync("ongoing-purchase-owner");
        workspace = (await CreateWorkspaceAsync(owner)).Id;
        category = (await ReadAsync<JsonElement>(await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{workspace}/categories", owner, new { name = "Compras sintéticas", type = "Expense" }))).GetProperty("id").GetGuid();
        account = (await ReadAsync<JsonElement>(await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{workspace}/accounts", owner, new { name = "Conta sintética", type = "Checking", financialScope = "PF", openingBalance = "5000.00", openingBalanceDate = "2020-01-01" }))).GetProperty("id").GetGuid();
        card = (await ReadAsync<JsonElement>(await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{workspace}/credit-cards", owner, new { name = "Cartão sintético", issuer = "Banco", creditLimit = "10000.00", closingDay = 5, dueDay = 10, defaultPaymentAccountId = account }))).GetProperty("id").GetGuid();
    }
}
