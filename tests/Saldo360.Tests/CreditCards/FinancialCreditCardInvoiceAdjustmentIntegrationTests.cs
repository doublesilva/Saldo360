using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Saldo360.Application.CreditCards;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.CreditCards;

[Collection(PostgresCollection.Name)]
public sealed class FinancialCreditCardInvoiceAdjustmentIntegrationTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    [Theory]
    [InlineData("PreviousBalance", "Debit")]
    [InlineData("FinancedBalance", "Credit")]
    [InlineData("999", "Debit")]
    [InlineData("Credit", "999")]
    public async Task ManualContractDoesNotAcceptInternalBalanceTypesOrUndefinedEnums(string type, string nature)
    {
        var context = await ArrangeAsync("adjustment-contract");
        await using var scope = Factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<FinancialCreditCardInvoiceAdjustmentService>();
        var result = await service.CreateAsync(context.WorkspaceId, context.CardId, context.InvoiceId, context.UserId, Input(type, nature, "1.00", "invalid-contract"), default);
        Assert.Equal(InvoiceAdjustmentAccess.Invalid, result.Access);
        await using var db = Db(); Assert.Empty(await db.FinancialCreditCardInvoiceAdjustments.ToListAsync());
    }

    [Fact]
    public async Task DebitAndCreditAdjustmentsAffectInvoiceLimitAndProjectionWithoutBankTransaction()
    {
        var context = await ArrangeAsync("adjustment-aggregates");
        await CreateAdjustmentAsync(context, "Fee", "Debit", "10.01", "pdf:fee");
        await CreateAdjustmentAsync(context, "Credit", "Credit", "20.02", "pdf:credit");
        await CreateAdjustmentAsync(context, "Refund", "Credit", "5.03", "pdf:refund");

        var invoice = await GetJsonAsync(context.Session, $"/api/financial-workspaces/{context.WorkspaceId}/credit-cards/{context.CardId}/invoices/{context.InvoiceId}");
        Assert.Equal("84.96", Money(invoice, "totalAmount"));
        Assert.Equal("84.96", Money(invoice, "remainingAmount"));
        var limit = await GetJsonAsync(context.Session, $"/api/financial-workspaces/{context.WorkspaceId}/credit-cards/{context.CardId}/limit");
        Assert.Equal("84.96", Money(limit, "usedLimit"));
        var projection = await GetJsonAsync(context.Session, $"/api/financial-workspaces/{context.WorkspaceId}/accounts/{context.AccountId}/projection?through=2026-12-31");
        Assert.Equal("84.96", Money(projection, "creditCardOutstanding"));

        await using var db = Db();
        Assert.Equal(3, await db.FinancialCreditCardInvoiceAdjustments.CountAsync());
        Assert.Empty(db.FinancialTransactions);
        Assert.Equal(10.01m, await db.FinancialCreditCardInvoiceAdjustments.Where(item => item.Nature == Domain.CreditCards.FinancialCreditCardInvoiceAdjustmentNature.Debit).SumAsync(item => item.Amount));
        Assert.Equal(25.05m, await db.FinancialCreditCardInvoiceAdjustments.Where(item => item.Nature == Domain.CreditCards.FinancialCreditCardInvoiceAdjustmentNature.Credit).SumAsync(item => item.Amount));
    }

    [Fact]
    public async Task CancellationIsIdempotentAndCannotReduceInvoiceBelowPaidAmount()
    {
        var context = await ArrangeAsync("adjustment-cancellation");
        var created = await CreateAdjustmentAsync(context, "Fee", "Debit", "20.00", "pdf:same");
        var replay = await CreateAdjustmentAsync(context, "Fee", "Debit", "20.00", "pdf:same");
        Assert.Equal(created.Id, replay.Id);

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<FinancialCreditCardInvoiceAdjustmentService>();
            var conflict = await service.CreateAsync(context.WorkspaceId, context.CardId, context.InvoiceId, context.UserId,
                Input("Fee", "Debit", "20.01", "pdf:same"), CancellationToken.None);
            Assert.Equal(InvoiceAdjustmentAccess.Conflict, conflict.Access);
        }

        await using (var official = Db()) await OfficialInvoiceFixture.CloseAsync(official, context.InvoiceId);
        var payment = await SendWithCsrfAsync(HttpMethod.Post,
            $"/api/financial-workspaces/{context.WorkspaceId}/credit-cards/{context.CardId}/invoices/{context.InvoiceId}/payments", context.Session,
            new { operationId = Guid.NewGuid(), financialAccountId = context.AccountId, paymentDate = "2026-10-17", amount = "110.00" });
        Assert.Equal(HttpStatusCode.Created, payment.StatusCode);

        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<FinancialCreditCardInvoiceAdjustmentService>();
            var blocked = await service.CancelAsync(context.WorkspaceId, context.CardId, context.InvoiceId, created.Id, context.UserId, CancellationToken.None);
            Assert.Equal(InvoiceAdjustmentAccess.Conflict, blocked.Access); // Official composition remains immutable even before considering the paid total.
        }
        await using var db = Db();
        Assert.Equal(1, await db.FinancialCreditCardInvoiceAdjustments.CountAsync());
        Assert.Equal(1, await db.FinancialCreditCardInvoicePayments.CountAsync());
        Assert.Equal(1, await db.FinancialTransactions.CountAsync());
    }

    [Fact]
    public async Task CancellationRemovesEffectAndFamilyIsolationIsEnforced()
    {
        var context = await ArrangeAsync("adjustment-isolation");
        var fee = await CreateAdjustmentAsync(context, "Fee", "Debit", "0.01", "pdf:precise");
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<FinancialCreditCardInvoiceAdjustmentService>();
            var foreign = await service.CreateAsync(Guid.NewGuid(), context.CardId, context.InvoiceId, context.UserId,
                Input("Fee", "Debit", "1.00", "pdf:foreign"), CancellationToken.None);
            Assert.Equal(InvoiceAdjustmentAccess.NotFound, foreign.Access);
            var cancelled = await service.CancelAsync(context.WorkspaceId, context.CardId, context.InvoiceId, fee.Id, context.UserId, CancellationToken.None);
            Assert.Equal(InvoiceAdjustmentAccess.Allowed, cancelled.Access);
            Assert.Equal("Cancelled", cancelled.Adjustment!.Status);
            var repeated = await service.CancelAsync(context.WorkspaceId, context.CardId, context.InvoiceId, fee.Id, context.UserId, CancellationToken.None);
            Assert.Equal("Cancelled", repeated.Adjustment!.Status);
        }
        var invoice = await GetJsonAsync(context.Session, $"/api/financial-workspaces/{context.WorkspaceId}/credit-cards/{context.CardId}/invoices/{context.InvoiceId}");
        Assert.Equal("100.00", Money(invoice, "totalAmount"));
        Assert.Equal("100.00", Money(await GetJsonAsync(context.Session, $"/api/financial-workspaces/{context.WorkspaceId}/credit-cards/{context.CardId}/limit"), "usedLimit"));
    }

    private async Task<TestContext> ArrangeAsync(string subject)
    {
        var session = await SignInAsync(subject);
        var me = await GetMeAsync(session);
        var family = await CreateWorkspaceAsync(session, "Família ajustes");
        await using var identityDb = Db();
        var userId = await identityDb.Users.Where(user => user.Issuer == me.Issuer && user.Subject == me.Subject).Select(user => user.Id).SingleAsync();
        var accountResponse = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{family.Id}/accounts", session,
            new { name = "Conta", type = "Checking", financialScope = "PF", openingBalance = "5000.00", openingBalanceDate = "2026-01-01" });
        var accountId = (await ReadAsync<JsonElement>(accountResponse)).GetProperty("id").GetGuid();
        var categoryResponse = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{family.Id}/categories", session,
            new { name = "Compras", type = "Expense" });
        var categoryId = (await ReadAsync<JsonElement>(categoryResponse)).GetProperty("id").GetGuid();
        var cardResponse = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{family.Id}/credit-cards", session,
            new { name = "Cartão", issuer = "Banco", holderUserId = userId, creditLimit = "1000.00", closingDay = 10, dueDay = 20, defaultPaymentAccountId = accountId });
        var cardId = (await ReadAsync<JsonElement>(cardResponse)).GetProperty("id").GetGuid();
        var purchase = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{family.Id}/credit-cards/{cardId}/purchases", session,
            new { description = "Compra base", purchaseDate = "2026-09-01", totalAmount = "100.00", installmentCount = 1, categoryId });
        Assert.Equal(HttpStatusCode.Created, purchase.StatusCode);
        var invoices = await GetJsonAsync(session, $"/api/financial-workspaces/{family.Id}/credit-cards/{cardId}/invoices?from=2026-09&through=2026-12");
        var invoiceId = invoices[0].GetProperty("id").GetGuid();
        return new(session, family.Id, userId, accountId, cardId, invoiceId);
    }

    private async Task<FinancialCreditCardInvoiceAdjustmentDto> CreateAdjustmentAsync(TestContext context, string type, string nature, string amount, string externalId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<FinancialCreditCardInvoiceAdjustmentService>();
        var result = await service.CreateAsync(context.WorkspaceId, context.CardId, context.InvoiceId, context.UserId,
            Input(type, nature, amount, externalId), CancellationToken.None);
        Assert.Equal(InvoiceAdjustmentAccess.Allowed, result.Access);
        return result.Adjustment!;
    }

    private static FinancialCreditCardInvoiceAdjustmentInput Input(string type, string nature, string amount, string externalId) =>
        new(type, nature, "2026-09-02", $"{type} teste", amount, externalId, null);

    private async Task<JsonElement> GetJsonAsync(string session, string url)
    {
        var response = await SendAsync(HttpMethod.Get, url, session);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<JsonElement>(response);
    }

    private static string Money(JsonElement element, string property) => element.GetProperty(property).GetString()!;
    private sealed record TestContext(string Session, Guid WorkspaceId, Guid UserId, Guid AccountId, Guid CardId, Guid InvoiceId);
}
