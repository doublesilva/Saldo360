using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Saldo360.Application.Pluggy;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Pluggy;

/// <summary>Base dos testes de API do laboratório Pluggy: API real sobre PostgreSQL com o cliente Pluggy simulado.</summary>
public abstract class PluggyApiTestBase(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    internal const string ItemA = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    internal const string ItemB = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    internal const string BankA = "a0000000-0000-4000-8000-000000000001";
    internal const string CardA = "a0000000-0000-4000-8000-000000000002";
    internal const string BankB = "b0000000-0000-4000-8000-000000000001";

    internal readonly FakePluggyClient Pluggy = Seeded();

    private static FakePluggyClient Seeded()
    {
        var client = new FakePluggyClient();
        client.Items[ItemA] = FakePluggyClient.Item(ItemA);
        client.Items[ItemB] = FakePluggyClient.Item(ItemB, "Nubank");
        client.AccountsByItem[ItemA] = [FakePluggyClient.Account(BankA, ItemA), FakePluggyClient.Account(CardA, ItemA, "CREDIT", "5555444433332222")];
        client.AccountsByItem[ItemB] = [FakePluggyClient.Account(BankB, ItemB)];
        return client;
    }

    protected HttpClient LabClient(bool importEnabled = true, Action<IServiceCollection>? configure = null) => Factory.WithWebHostBuilder(builder =>
    {
        builder.UseSetting("Pluggy:Enabled", "true");
        builder.UseSetting("Pluggy:ImportEnabled", importEnabled ? "true" : "false");
        builder.UseSetting("Pluggy:ClientId", "client-sintetico");
        builder.UseSetting("Pluggy:ClientSecret", "segredo-que-nao-pode-vazar");
        builder.UseSetting("Pluggy:SyncPageDelayMilliseconds", "0");
        builder.UseSetting("Pluggy:SyncRateLimitDelaySeconds", "0");
        builder.UseSetting("Pluggy:SyncRateLimitRetries", "2");
        builder.ConfigureTestServices(services => { services.AddSingleton<IPluggyClient>(Pluggy); configure?.Invoke(services); });
    }).CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), HandleCookies = false, AllowAutoRedirect = false });

    protected async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string url, string session, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (method == HttpMethod.Get) request.Headers.Add("Cookie", session);
        else
        {
            var (cookie, token) = await GetCsrfAsync(session);
            request.Headers.Add("Cookie", $"{session}; {cookie}");
            request.Headers.Add("X-CSRF-TOKEN", token);
            if (body is not null) request.Content = JsonContent.Create(body);
        }
        return await client.SendAsync(request);
    }

    protected static string Root(Guid workspace) => $"/api/financial-workspaces/{workspace}/pluggy-poc";

    protected async Task<Guid> LinkAsync(HttpClient client, string session, Guid workspace, string item, string label = "Item")
    {
        var created = await Send(client, HttpMethod.Post, $"{Root(workspace)}/connections", session, new { itemId = item, label });
        Assert.Equal(System.Net.HttpStatusCode.Created, created.StatusCode);
        return JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    }

    protected static async Task<JsonElement> JsonOf(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
}
