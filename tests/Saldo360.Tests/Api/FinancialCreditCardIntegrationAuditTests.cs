using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Domain.Common;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

/// <summary>
/// Cenários de ponta a ponta da T09.7. PostgreSQL real é usado para verificar as invariantes entre
/// compra, parcelas, fatura, pagamento, conta, projeção e orçamento.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FinancialCreditCardIntegrationAuditTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private static readonly DateOnly OpeningDate = new(2026, 1, 1);
    private static readonly DateOnly PurchaseDate = new(2026, 9, 1);
    private string _owner = null!;
    private WorkspaceDto _family = null!;
    private Guid _account;
    private Guid _category;
    private Guid _card;

    private static DateOnly Today => BusinessCalendar.Today(DateTimeOffset.UtcNow);

    [Fact]
    public async Task InstallmentsPaymentAndCancellationRemainConsistentAcrossFinancialModules()
    {
        await ArrangeAsync();

        var purchase = await SendWithCsrfAsync(HttpMethod.Post,
            $"/api/financial-workspaces/{_family.Id}/credit-cards/{_card}/purchases", _owner,
            new { description = "Compra auditada", purchaseDate = PurchaseDate, totalAmount = "1200.00", installmentCount = 3, categoryId = _category });
        Assert.Equal(HttpStatusCode.Created, purchase.StatusCode);

        var limitBefore = await GetJsonAsync($"/api/financial-workspaces/{_family.Id}/credit-cards/{_card}/limit");
        Assert.Equal(("1200.00", "8800.00"), (Money(limitBefore, "usedLimit"), Money(limitBefore, "availableLimit")));

        var invoices = await GetJsonAsync($"/api/financial-workspaces/{_family.Id}/credit-cards/{_card}/invoices?from=2026-09&through=2026-11");
        Assert.Equal(3, invoices.GetArrayLength());
        Assert.All(invoices.EnumerateArray(), invoice => Assert.Equal("400.00", Money(invoice, "totalAmount")));

        var budget = await CreateBudgetAsync("2026-09");
        var target = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{_family.Id}/budgets/{budget}/targets", _owner,
            new { categoryId = _category, name = "Compras", groupingType = "Category", plannedAmount = "2000.00" });
        Assert.Equal(HttpStatusCode.Created, target.StatusCode);
        var execution = await GetJsonAsync($"/api/financial-workspaces/{_family.Id}/budgets/{budget}/execution");
        Assert.Equal("400.00", Money(execution.GetProperty("total"), "creditCardCommitted"));
        Assert.Equal("400.00", Money(execution.GetProperty("total"), "projectedConsumption"));

        var projectionBefore = await GetJsonAsync($"/api/financial-workspaces/{_family.Id}/accounts/{_account}/projection?through=2026-11-30");
        Assert.Equal("1200.00", Money(projectionBefore, "creditCardOutstanding"));
        Assert.Equal("3800.00", Money(projectionBefore, "projectedBalance"));
        Assert.Equal("5000.00", Money(await GetJsonAsync($"/api/financial-workspaces/{_family.Id}/accounts/{_account}/balance?asOf={Today:yyyy-MM-dd}"), "balance"));

        var firstInvoiceId = invoices[0].GetProperty("id").GetGuid();
        await using (var official = Db()) await OfficialInvoiceFixture.CloseAsync(official, firstInvoiceId);
        var paymentOperationId = Guid.NewGuid();
        var paymentPayload = new { operationId = paymentOperationId, financialAccountId = _account, paymentDate = Today, amount = "400.00" };
        var paymentUrl = $"/api/financial-workspaces/{_family.Id}/credit-cards/{_card}/invoices/{firstInvoiceId}/payments";
        var concurrentPayments = await Task.WhenAll(
            SendWithCsrfAsync(HttpMethod.Post, paymentUrl, _owner, paymentPayload),
            SendWithCsrfAsync(HttpMethod.Post, paymentUrl, _owner, paymentPayload));
        var payment = concurrentPayments[0];
        var replay = concurrentPayments[1];
        Assert.Equal(HttpStatusCode.Created, payment.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        var paymentBody = await ReadAsync<JsonElement>(payment);
        Assert.Equal(paymentOperationId, paymentBody.GetProperty("operationId").GetGuid());
        Assert.Equal(paymentBody.GetProperty("id").GetGuid(), (await ReadAsync<JsonElement>(replay)).GetProperty("id").GetGuid());
        var changedPayload = await SendWithCsrfAsync(HttpMethod.Post,
            $"/api/financial-workspaces/{_family.Id}/credit-cards/{_card}/invoices/{firstInvoiceId}/payments", _owner,
            new { operationId = paymentOperationId, financialAccountId = _account, paymentDate = Today, amount = "399.99" });
        Assert.Equal(HttpStatusCode.Conflict, changedPayload.StatusCode);

        var afterPayment = await GetJsonAsync($"/api/financial-workspaces/{_family.Id}/credit-cards/{_card}/limit");
        Assert.Equal(("800.00", "9200.00"), (Money(afterPayment, "usedLimit"), Money(afterPayment, "availableLimit")));
        var paidInvoice = await GetJsonAsync($"/api/financial-workspaces/{_family.Id}/credit-cards/{_card}/invoices/{firstInvoiceId}");
        Assert.Equal(("400.00", "400.00", "0.00", "Paid"),
            (Money(paidInvoice, "totalAmount"), Money(paidInvoice, "paidAmount"), Money(paidInvoice, "remainingAmount"), paidInvoice.GetProperty("paymentStatus").GetString()));
        var paidBalance = await GetJsonAsync($"/api/financial-workspaces/{_family.Id}/accounts/{_account}/balance?asOf={Today:yyyy-MM-dd}");
        Assert.Equal("4600.00", Money(paidBalance, "balance"));
        var projectedAfterPayment = await GetJsonAsync($"/api/financial-workspaces/{_family.Id}/accounts/{_account}/projection?through=2026-11-30");
        Assert.Equal(("800.00", "3800.00"), (Money(projectedAfterPayment, "creditCardOutstanding"), Money(projectedAfterPayment, "projectedBalance")));

        var paymentId = paymentBody.GetProperty("id").GetGuid();
        var cancelled = await SendWithCsrfAsync(HttpMethod.Post,
            $"/api/financial-workspaces/{_family.Id}/credit-cards/{_card}/invoices/{firstInvoiceId}/payments/{paymentId}/cancel", _owner);
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendWithCsrfAsync(HttpMethod.Post,
            $"/api/financial-workspaces/{_family.Id}/credit-cards/{_card}/invoices/{firstInvoiceId}/payments/{paymentId}/cancel", _owner)).StatusCode);
        var replayAfterCancel = await SendWithCsrfAsync(HttpMethod.Post, paymentUrl, _owner, paymentPayload);
        Assert.Equal(HttpStatusCode.Created, replayAfterCancel.StatusCode);
        var replayAfterCancelBody = await ReadAsync<JsonElement>(replayAfterCancel);
        Assert.Equal(paymentId, replayAfterCancelBody.GetProperty("id").GetGuid());
        Assert.Equal("Cancelled", replayAfterCancelBody.GetProperty("status").GetString());
        var restored = await GetJsonAsync($"/api/financial-workspaces/{_family.Id}/credit-cards/{_card}/limit");
        Assert.Equal(("1200.00", "8800.00"), (Money(restored, "usedLimit"), Money(restored, "availableLimit")));
        Assert.Equal("5000.00", Money(await GetJsonAsync($"/api/financial-workspaces/{_family.Id}/accounts/{_account}/balance?asOf={Today:yyyy-MM-dd}"), "balance"));
        Assert.Equal("1200.00", Money(await GetJsonAsync($"/api/financial-workspaces/{_family.Id}/accounts/{_account}/projection?through=2026-11-30"), "creditCardOutstanding"));

        var partial = await SendWithCsrfAsync(HttpMethod.Post,
            $"/api/financial-workspaces/{_family.Id}/credit-cards/{_card}/invoices/{firstInvoiceId}/payments", _owner,
            new { operationId = Guid.NewGuid(), financialAccountId = _account, paymentDate = Today, amount = "100.00" });
        Assert.Equal(HttpStatusCode.Created, partial.StatusCode);
        var partiallyPaid = await GetJsonAsync($"/api/financial-workspaces/{_family.Id}/credit-cards/{_card}/invoices/{firstInvoiceId}");
        Assert.Equal(("100.00", "300.00", "PartiallyPaid"),
            (Money(partiallyPaid, "paidAmount"), Money(partiallyPaid, "remainingAmount"), partiallyPaid.GetProperty("paymentStatus").GetString()));
        Assert.Equal("1100.00", Money(await GetJsonAsync($"/api/financial-workspaces/{_family.Id}/credit-cards/{_card}/limit"), "usedLimit"));
        await using var db = Db();
        Assert.Equal(2, await db.FinancialCreditCardInvoicePayments.CountAsync());
        Assert.Equal(2, await db.FinancialTransactions.CountAsync(transaction => transaction.Origin == Saldo360.Domain.Transactions.FinancialTransactionOrigin.CreditCardInvoicePayment));
    }

    [Fact]
    public async Task CardWithoutPreferredAccountIsMarkedIncompleteAndNeverAssignedArbitrarily()
    {
        await ArrangeAsync(withPreferredAccount: false);
        var purchase = await SendWithCsrfAsync(HttpMethod.Post,
            $"/api/financial-workspaces/{_family.Id}/credit-cards/{_card}/purchases", _owner,
            new { description = "Sem conta", purchaseDate = PurchaseDate, totalAmount = "0.01", installmentCount = 1, categoryId = _category });
        Assert.Equal(HttpStatusCode.Created, purchase.StatusCode);

        var projection = await GetJsonAsync($"/api/financial-workspaces/{_family.Id}/accounts/{_account}/projection?through=2026-11-30");
        Assert.Equal("0.00", Money(projection, "creditCardOutstanding"));
        Assert.False(projection.GetProperty("creditCardProjectionComplete").GetBoolean());
        Assert.Equal("5000.00", Money(projection, "projectedBalance"));
    }

    private async Task ArrangeAsync(bool withPreferredAccount = true)
    {
        _owner = await SignInAsync("credit-audit-owner");
        _family = await CreateWorkspaceAsync(_owner, "Família Auditoria Cartões");
        _account = await CreateAccountAsync("Conta principal", "5000.00", OpeningDate);
        _category = await CreateCategoryAsync("Compras", "Expense");
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{_family.Id}/credit-cards", _owner,
            new { name = "Cartão auditoria", issuer = "Banco", creditLimit = "10000.00", closingDay = 10, dueDay = 20,
                defaultPaymentAccountId = withPreferredAccount ? _account : (Guid?)null });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        _card = (await ReadAsync<JsonElement>(response)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateAccountAsync(string name, string openingBalance, DateOnly openingBalanceDate)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{_family.Id}/accounts", _owner,
            new { name, type = "Checking", financialScope = "PF", openingBalance, openingBalanceDate });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<JsonElement>(response)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateCategoryAsync(string name, string type)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{_family.Id}/categories", _owner, new { name, type });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<JsonElement>(response)).GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateBudgetAsync(string competence)
    {
        var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{_family.Id}/budgets", _owner, new { competence, description = "Auditoria" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<JsonElement>(response)).GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> GetJsonAsync(string url)
    {
        var response = await SendAsync(HttpMethod.Get, url, _owner);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<JsonElement>(response);
    }

    private static string Money(JsonElement json, string property) => json.GetProperty(property).GetString()!;
}
