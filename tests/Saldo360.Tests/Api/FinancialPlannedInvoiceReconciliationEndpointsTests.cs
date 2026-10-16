using System.Net;
using System.Text.Json;
using Saldo360.Domain.Common;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

[Collection(PostgresCollection.Name)]
public sealed class FinancialPlannedInvoiceReconciliationEndpointsTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private static readonly DateOnly Today = BusinessCalendar.Today(DateTimeOffset.UtcNow);
    private string _owner = null!;
    private FamilyDto _family = null!;
    private Guid _account;
    private Guid _category;
    private Guid _planned;
    private Guid _invoice;
    private Guid _card;

    [Fact]
    public async Task LinkQueryAndUnlinkUseOneObligationAndPreserveOriginalPrediction()
    {
        await ArrangeAsync();
        var before = await ProjectionAsync();
        Assert.Equal(("4200.00", "400.00"), (Money(before, "projectedBalance"), Money(before, "creditCardOutstanding")));

        var created = await SendWithCsrfAsync(HttpMethod.Post, ReconciliationsUrl, _owner,
            new { plannedTransactionId = _planned, creditCardInvoiceId = _invoice });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var reconciliation = await ReadAsync<JsonElement>(created);
        var reconciliationId = reconciliation.GetProperty("id").GetGuid();
        var replay = await SendWithCsrfAsync(HttpMethod.Post, ReconciliationsUrl, _owner,
            new { plannedTransactionId = _planned, creditCardInvoiceId = _invoice });
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(reconciliationId, (await ReadAsync<JsonElement>(replay)).GetProperty("id").GetGuid());

        var listed = await GetJsonAsync(ReconciliationsUrl);
        Assert.Single(listed.EnumerateArray());
        Assert.Equal(_planned, listed[0].GetProperty("plannedTransactionId").GetGuid());

        var linked = await ProjectionAsync();
        Assert.Equal(("4600.00", "400.00"), (Money(linked, "projectedBalance"), Money(linked, "creditCardOutstanding")));
        Assert.Equal(HttpStatusCode.Conflict, (await SendWithCsrfAsync(HttpMethod.Post, $"{PlannedUrl}/{_planned}/cancel", _owner)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await SendWithCsrfAsync(HttpMethod.Put, $"{PlannedUrl}/{_planned}", _owner,
            new { accountId = _account, categoryId = _category, type = "Expense", description = "Alterada", amount = "400.00", dueDate = Today })).StatusCode);

        var removed = await SendWithCsrfAsync(HttpMethod.Delete, $"{ReconciliationsUrl}/{reconciliationId}", _owner);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendWithCsrfAsync(HttpMethod.Delete, $"{ReconciliationsUrl}/{reconciliationId}", _owner)).StatusCode);
        var unlinked = await ProjectionAsync();
        Assert.Equal("4200.00", Money(unlinked, "projectedBalance"));
        Assert.Equal(1, (await GetJsonAsync($"{ReconciliationsUrl}?plannedTransactionId={_planned}")).GetArrayLength());
        Assert.Equal("Pending", (await GetJsonAsync($"{PlannedUrl}/{_planned}")).GetProperty("status").GetString());
    }

    [Fact]
    public async Task AccountCompatibilityAndFamilyIsolationAreRequired()
    {
        await ArrangeAsync();
        var otherAccount = await CreateAccountAsync("Outra conta", "0.00");
        var incompatible = await CreatePlannedAsync(otherAccount, "400.00");
        var rejected = await SendWithCsrfAsync(HttpMethod.Post, ReconciliationsUrl, _owner,
            new { plannedTransactionId = incompatible, creditCardInvoiceId = _invoice });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        var outsider = await SignInAsync("reconciliation-outsider");
        var otherFamily = await CreateFamilyAsync(outsider, "Família externa");
        Assert.Equal(HttpStatusCode.NotFound, (await SendWithCsrfAsync(HttpMethod.Post,
            $"/api/families/{otherFamily.Id}/invoice-reconciliations", outsider,
            new { plannedTransactionId = _planned, creditCardInvoiceId = _invoice })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Get, ReconciliationsUrl, outsider)).StatusCode);
    }

    private string ReconciliationsUrl => $"/api/families/{_family.Id}/invoice-reconciliations";
    private string PlannedUrl => $"/api/families/{_family.Id}/planned-transactions";

    private async Task ArrangeAsync()
    {
        _owner = await SignInAsync("reconciliation-owner");
        _family = await CreateFamilyAsync(_owner, "Família Conciliação");
        _account = await CreateAccountAsync("Conta principal", "5000.00");
        _category = await CreateCategoryAsync("Fatura", "Expense");
        var cardResponse = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{_family.Id}/credit-cards", _owner,
            new { name = "Cartão", issuer = "Banco", creditLimit = "10000.00", closingDay = 10, dueDay = 20, defaultPaymentAccountId = _account });
        Assert.Equal(HttpStatusCode.Created, cardResponse.StatusCode);
        _card = (await ReadAsync<JsonElement>(cardResponse)).GetProperty("id").GetGuid();
        var purchaseResponse = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{_family.Id}/credit-cards/{_card}/purchases", _owner,
            new { description = "Fatura", purchaseDate = new DateOnly(2026, 9, 1), totalAmount = "400.00", installmentCount = 1, categoryId = _category });
        Assert.Equal(HttpStatusCode.Created, purchaseResponse.StatusCode);
        var invoices = await GetJsonAsync($"/api/families/{_family.Id}/credit-cards/{_card}/invoices?from=2026-09&through=2026-09");
        _invoice = invoices[0].GetProperty("id").GetGuid();
        _planned = await CreatePlannedAsync(_account, "400.00");
    }

    private async Task<JsonElement> ProjectionAsync() => await GetJsonAsync($"/api/families/{_family.Id}/accounts/{_account}/projection?through=2026-11-30");

    private async Task<Guid> CreatePlannedAsync(Guid account, string amount) { var response = await SendWithCsrfAsync(HttpMethod.Post, PlannedUrl, _owner, new { accountId = account, categoryId = _category, type = "Expense", description = "Pagamento de fatura", amount, dueDate = Today }); Assert.Equal(HttpStatusCode.Created, response.StatusCode); return (await ReadAsync<JsonElement>(response)).GetProperty("id").GetGuid(); }
    private async Task<Guid> CreateAccountAsync(string name, string balance) { var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{_family.Id}/accounts", _owner, new { name, type = "Checking", financialScope = "PF", openingBalance = balance, openingBalanceDate = new DateOnly(2026, 1, 1) }); Assert.Equal(HttpStatusCode.Created, response.StatusCode); return (await ReadAsync<JsonElement>(response)).GetProperty("id").GetGuid(); }
    private async Task<Guid> CreateCategoryAsync(string name, string type) { var response = await SendWithCsrfAsync(HttpMethod.Post, $"/api/families/{_family.Id}/categories", _owner, new { name, type }); Assert.Equal(HttpStatusCode.Created, response.StatusCode); return (await ReadAsync<JsonElement>(response)).GetProperty("id").GetGuid(); }
    private async Task<JsonElement> GetJsonAsync(string url) { var response = await SendAsync(HttpMethod.Get, url, _owner); Assert.Equal(HttpStatusCode.OK, response.StatusCode); return await ReadAsync<JsonElement>(response); }
    private static string Money(JsonElement json, string property) => json.GetProperty(property).GetString()!;
}
