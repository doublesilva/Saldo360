using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Saldo360.Application.Pluggy;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Pluggy;

[Collection(PostgresCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Pluggy")]
[Trait("Dependency", "PostgreSQL")]
public sealed class PluggyPocEndpointsTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    private const string ItemId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string BankId = "a0000000-0000-4000-8000-000000000001";
    private const string CardId = "a0000000-0000-4000-8000-000000000002";

    private readonly FakePluggyClient pluggy = SeededClient();

    private static FakePluggyClient SeededClient()
    {
        var client = new FakePluggyClient();
        client.Items[ItemId] = FakePluggyClient.Item(ItemId);
        client.AccountsByItem[ItemId] = [FakePluggyClient.Account(BankId, ItemId), FakePluggyClient.Account(CardId, ItemId, "CREDIT", "5555444433332222")];
        client.TransactionsByAccount[BankId] = [FakePluggyClient.Transaction("t1", BankId, "2026-10-01", -10.25m), FakePluggyClient.Transaction("t2", BankId, "2026-10-02", 99.9m), FakePluggyClient.Transaction("t3", BankId, "2026-10-03", -1m)];
        client.BillsByAccount[CardId] = [new("bill-1", DateTimeOffset.Parse("2026-10-17T00:00:00Z"), 1500.25m, "BRL", 225.04m, true, [], null, [])];
        return client;
    }

    private HttpClient EnabledClient(bool enabled = true) => Factory.WithWebHostBuilder(builder =>
    {
        builder.UseSetting("Pluggy:Enabled", enabled ? "true" : "false");
        builder.UseSetting("Pluggy:ClientId", "client-sintetico");
        builder.UseSetting("Pluggy:ClientSecret", "segredo-que-nao-pode-vazar");
        builder.ConfigureTestServices(services => services.AddSingleton<IPluggyClient>(pluggy));
    }).CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });

    private async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string url, string session, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Cookie", session);
        if (body is not null || method != HttpMethod.Get)
        {
            var (cookie, token) = await GetCsrfAsync(session);
            request.Headers.Remove("Cookie");
            request.Headers.Add("Cookie", $"{session}; {cookie}");
            request.Headers.Add("X-CSRF-TOKEN", token);
            if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body, Json), System.Text.Encoding.UTF8, "application/json");
        }
        return await client.SendAsync(request);
    }

    private static string Root(Guid workspace) => $"/api/financial-workspaces/{workspace}/pluggy-poc";

    [Fact]
    public async Task EndpointsRequireAuthentication()
    {
        using var client = EnabledClient();
        var workspace = Guid.NewGuid();
        foreach (var path in new[] { "status", "connections", $"connections/{Guid.NewGuid()}/accounts", $"connections/{Guid.NewGuid()}/diagnostics", $"accounts/{BankId}/transactions", $"accounts/{CardId}/bills" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"{Root(workspace)}/{path}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync($"{Root(workspace)}/connections", null)).StatusCode);
    }

    [Fact]
    public async Task LaboratoryIsHiddenWhenNotEnabled()
    {
        var session = await SignInAsync("pluggy-disabled");
        var workspace = await CreateWorkspaceAsync(session);
        using var client = EnabledClient(enabled: false);

        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, HttpMethod.Get, $"{Root(workspace.Id)}/status", session)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, HttpMethod.Get, $"{Root(workspace.Id)}/connections", session)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, HttpMethod.Post, $"{Root(workspace.Id)}/connections", session, new { itemId = ItemId })).StatusCode);
        await using var db = Db();
        Assert.Equal(0, await db.PluggyItemLinks.CountAsync());
        Assert.Empty(pluggy.Calls);
    }

    [Fact]
    public async Task OwnerRegistersItemAndReadsAccountsTransactionsBillsAndDiagnostics()
    {
        var session = await SignInAsync("pluggy-owner");
        var workspace = await CreateWorkspaceAsync(session);
        using var client = EnabledClient();

        Assert.Equal(HttpStatusCode.OK, (await Send(client, HttpMethod.Get, $"{Root(workspace.Id)}/status", session)).StatusCode);
        var created = await Send(client, HttpMethod.Post, $"{Root(workspace.Id)}/connections", session, new { itemId = ItemId, label = "Itaú PF" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("no-store", created.Headers.CacheControl?.ToString());
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var connectionId = createdBody.RootElement.GetProperty("id").GetGuid();
        Assert.Equal("Itaú", createdBody.RootElement.GetProperty("institution").GetString());
        Assert.Equal("Healthy", createdBody.RootElement.GetProperty("state").GetString());
        Assert.DoesNotContain(ItemId, createdBody.RootElement.ToString());

        var list = await Send(client, HttpMethod.Get, $"{Root(workspace.Id)}/connections", session);
        Assert.Equal(connectionId, JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement[0].GetProperty("id").GetGuid());

        var accountsResponse = await Send(client, HttpMethod.Get, $"{Root(workspace.Id)}/connections/{connectionId}/accounts", session);
        var accountsJson = await accountsResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, accountsResponse.StatusCode);
        Assert.Contains("••••6789", accountsJson);
        Assert.DoesNotContain("12345-6789", accountsJson);
        Assert.DoesNotContain("5555444433332222", accountsJson);

        var page = await Send(client, HttpMethod.Get, $"{Root(workspace.Id)}/accounts/{BankId}/transactions?from=2026-10-01&to=2026-10-31", session);
        using var pageBody = JsonDocument.Parse(await page.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal(2, pageBody.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal("-10.25", pageBody.RootElement.GetProperty("items")[0].GetProperty("amount").GetString());
        var cursor = pageBody.RootElement.GetProperty("nextCursor").GetString();
        var next = await Send(client, HttpMethod.Get, $"{Root(workspace.Id)}/accounts/{BankId}/transactions?after={cursor}", session);
        Assert.Equal(1, JsonDocument.Parse(await next.Content.ReadAsStringAsync()).RootElement.GetProperty("items").GetArrayLength());

        var bills = await Send(client, HttpMethod.Get, $"{Root(workspace.Id)}/accounts/{CardId}/bills", session);
        Assert.Equal("1500.25", JsonDocument.Parse(await bills.Content.ReadAsStringAsync()).RootElement.GetProperty("items")[0].GetProperty("totalAmount").GetString());

        var diagnostics = await Send(client, HttpMethod.Get, $"{Root(workspace.Id)}/connections/{connectionId}/diagnostics", session);
        Assert.Equal(HttpStatusCode.OK, diagnostics.StatusCode);
        Assert.Contains("Transações", await diagnostics.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OtherUsersCannotReadOrReuseTheItem()
    {
        var owner = await SignInAsync("pluggy-isolation-owner");
        var other = await SignInAsync("pluggy-isolation-other");
        var ownerWorkspace = await CreateWorkspaceAsync(owner);
        var otherWorkspace = await CreateWorkspaceAsync(other);
        using var client = EnabledClient();
        var created = await Send(client, HttpMethod.Post, $"{Root(ownerWorkspace.Id)}/connections", owner, new { itemId = ItemId });
        var connectionId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        // Outro usuário em outro orçamento: nada do primeiro é visível, nem por ids internos nem pelos da Pluggy.
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, HttpMethod.Get, $"{Root(otherWorkspace.Id)}/connections/{connectionId}/accounts", other)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, HttpMethod.Get, $"{Root(otherWorkspace.Id)}/connections/{connectionId}/diagnostics", other)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, HttpMethod.Get, $"{Root(otherWorkspace.Id)}/accounts/{BankId}/transactions", other)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, HttpMethod.Get, $"{Root(otherWorkspace.Id)}/accounts/{CardId}/bills", other)).StatusCode);
        Assert.Empty(JsonDocument.Parse(await (await Send(client, HttpMethod.Get, $"{Root(otherWorkspace.Id)}/connections", other)).Content.ReadAsStringAsync()).RootElement.EnumerateArray().ToArray());
        // Acesso ao orçamento alheio.
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, HttpMethod.Get, $"{Root(ownerWorkspace.Id)}/connections", other)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(client, HttpMethod.Post, $"{Root(ownerWorkspace.Id)}/connections", other, new { itemId = ItemId })).StatusCode);
        // O mesmo Item não pode ser vinculado por outro usuário.
        Assert.Equal(HttpStatusCode.Conflict, (await Send(client, HttpMethod.Post, $"{Root(otherWorkspace.Id)}/connections", other, new { itemId = ItemId })).StatusCode);
        await using var db = Db();
        Assert.Equal(1, await db.PluggyItemLinks.CountAsync());
    }

    [Fact]
    public async Task RegistrationRequiresCsrfAndValidatesPayload()
    {
        var session = await SignInAsync("pluggy-validation");
        var workspace = await CreateWorkspaceAsync(session);
        using var client = EnabledClient();

        using var withoutCsrf = new HttpRequestMessage(HttpMethod.Post, $"{Root(workspace.Id)}/connections") { Content = JsonContent.Create(new { itemId = ItemId }) };
        withoutCsrf.Headers.Add("Cookie", session);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(withoutCsrf)).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await Send(client, HttpMethod.Post, $"{Root(workspace.Id)}/connections", session, new { itemId = "nao-e-uuid" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(client, HttpMethod.Post, $"{Root(workspace.Id)}/connections", session, new { itemId = ItemId, clientSecret = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(client, HttpMethod.Get, $"{Root(workspace.Id)}/accounts/{BankId}/transactions?from=01/10/2026", session)).StatusCode);
        await using var db = Db();
        Assert.Equal(0, await db.PluggyItemLinks.CountAsync());
    }

    [Fact]
    public async Task IntegrationFailuresBecomeControlledErrorsWithoutProviderDetails()
    {
        var session = await SignInAsync("pluggy-errors");
        var workspace = await CreateWorkspaceAsync(session);
        using var client = EnabledClient();
        await Send(client, HttpMethod.Post, $"{Root(workspace.Id)}/connections", session, new { itemId = ItemId });

        foreach (var (kind, status, code) in new[]
        {
            (PluggyFailureKind.RateLimited, HttpStatusCode.TooManyRequests, "PLUGGY_RATE_LIMITED"),
            (PluggyFailureKind.Timeout, HttpStatusCode.GatewayTimeout, "PLUGGY_TIMEOUT"),
            (PluggyFailureKind.Unavailable, HttpStatusCode.ServiceUnavailable, "PLUGGY_UNAVAILABLE"),
            (PluggyFailureKind.NotConfigured, HttpStatusCode.ServiceUnavailable, "PLUGGY_NOT_CONFIGURED"),
            (PluggyFailureKind.Authentication, HttpStatusCode.BadGateway, "PLUGGY_AUTHENTICATION_FAILED"),
        })
        {
            pluggy.Failures[$"transactions:{BankId}"] = new PluggyException(kind, 500);
            var response = await Send(client, HttpMethod.Get, $"{Root(workspace.Id)}/accounts/{BankId}/transactions", session);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Equal(status, response.StatusCode);
            Assert.Contains(code, body);
            Assert.DoesNotContain("segredo-que-nao-pode-vazar", body);
            Assert.DoesNotContain(ItemId, body);
        }
    }

    [Fact]
    public async Task UsingTheLaboratoryNeverChangesOperationalFinancialData()
    {
        var session = await SignInAsync("pluggy-readonly");
        var workspace = await CreateWorkspaceAsync(session);
        var account = await SendWithCsrfAsync(HttpMethod.Post, $"/api/financial-workspaces/{workspace.Id}/accounts", session,
            new { name = "Conta", type = "Checking", financialScope = "PF", openingBalance = "100.00", openingBalanceDate = "2026-01-01" });
        Assert.Equal(HttpStatusCode.Created, account.StatusCode);
        var before = await Snapshot();

        using var client = EnabledClient();
        var created = await Send(client, HttpMethod.Post, $"{Root(workspace.Id)}/connections", session, new { itemId = ItemId });
        var connectionId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        await Send(client, HttpMethod.Get, $"{Root(workspace.Id)}/connections/{connectionId}/accounts", session);
        await Send(client, HttpMethod.Get, $"{Root(workspace.Id)}/accounts/{BankId}/transactions", session);
        await Send(client, HttpMethod.Get, $"{Root(workspace.Id)}/accounts/{CardId}/bills", session);
        await Send(client, HttpMethod.Get, $"{Root(workspace.Id)}/connections/{connectionId}/diagnostics", session);

        Assert.Equal(before, await Snapshot());
        Assert.All(pluggy.Calls, call => Assert.DoesNotContain("pay", call, StringComparison.OrdinalIgnoreCase));

        async Task<int[]> Snapshot()
        {
            await using var db = Db();
            return
            [
                await db.FinancialAccounts.CountAsync(), await db.FinancialTransactions.CountAsync(), await db.FinancialTransfers.CountAsync(),
                await db.FinancialPlannedTransactions.CountAsync(), await db.FinancialCreditCards.CountAsync(), await db.FinancialCreditCardPurchases.CountAsync(),
                await db.FinancialCreditCardInvoices.CountAsync(), await db.FinancialCreditCardImportedInstallments.CountAsync(), await db.FinancialDebts.CountAsync(),
            ];
        }
    }

    [Fact]
    public async Task ExposesNoRouteForPaymentsOrTransfers()
    {
        var session = await SignInAsync("pluggy-no-payments");
        var workspace = await CreateWorkspaceAsync(session);
        using var client = EnabledClient();
        foreach (var path in new[] { "payments", "transfers", "payment-intents", "connect-token" })
            Assert.Equal(HttpStatusCode.NotFound, (await Send(client, HttpMethod.Post, $"{Root(workspace.Id)}/{path}", session, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await Send(client, HttpMethod.Delete, $"{Root(workspace.Id)}/connections", session)).StatusCode);
    }
}
