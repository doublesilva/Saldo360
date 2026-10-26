using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Tests.Support;
using Saldo360.Tests.CreditCards;

namespace Saldo360.Tests.Api;

[Trait("Category", "CORE")]
[Trait("Area", "Services")]
public sealed class ServiceAccountApiTests() : ApiTestBase(settings: ServiceAccountResolverTests.Settings)
{
    private const string Root = "/api/me/service-accounts";
    private Task<HttpResponseMessage> SendJsonAsync(HttpMethod method, string path, string cookies, string? csrf, object body)
    {
        var request = new HttpRequestMessage(method, path) { Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Add("Cookie", cookies);
        if (csrf is not null) request.Headers.Add("X-CSRF-TOKEN", csrf);
        return Client.SendAsync(request);
    }
    [Fact]
    public async Task PersonalAccountsDoNotRequireFamilyAndNeverExposeRawIdentifiers()
    {
        var a = await SignInAsync("service-owner-a"); var b = await SignInAsync("service-owner-b");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync(Root)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendJsonAsync(HttpMethod.Post, Root, a, null,
            new { serviceType = "Electricity", issuer = "Equatorial", displayName = "Synthetic" })).StatusCode);
        var (cookie, token) = await GetCsrfAsync(a);
        var created = await SendJsonAsync(HttpMethod.Post, Root, a + "; " + cookie, token,
            new { serviceType = "Electricity", issuer = "CEEE", displayName = "Synthetic service" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
        var identifier = await SendJsonAsync(HttpMethod.Post, Root + "/" + id + "/identifiers", a + "; " + cookie, token,
            new { type = "UtilityCustomerNumber", value = "987.654.321-00" });
        Assert.Equal(HttpStatusCode.Created, identifier.StatusCode);
        var list = await SendAsync(HttpMethod.Get, Root, a);
        var json = await list.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Contains("Equatorial", json); Assert.Contains("***2100", json);
        Assert.DoesNotContain("98765432100", json); Assert.DoesNotContain("987.654.321-00", json);
        Assert.DoesNotContain("protectedNormalizedValue", json); Assert.DoesNotContain("lookupHash", json);
        Assert.Equal("[]", await (await SendAsync(HttpMethod.Get, Root, b)).Content.ReadAsStringAsync());
        var (cookieB, tokenB) = await GetCsrfAsync(b);
        Assert.Equal(HttpStatusCode.NotFound, (await SendJsonAsync(HttpMethod.Post, Root + "/" + id + "/identifiers", b + "; " + cookieB, tokenB,
            new { type = "UtilityCustomerNumber", value = "11122233344" })).StatusCode);
        await WithDbAsync(async db =>
        {
            Assert.False(await db.Families.AnyAsync()); Assert.False(await db.FinancialTransactions.AnyAsync());
            Assert.False(await db.FinancialPlannedTransactions.AnyAsync()); Assert.False(await db.ServiceAccountBindings.AnyAsync());
        });
        Assert.DoesNotContain(Factory.Logs.Entries, message => message.Contains("98765432100") || message.Contains("987.654.321-00"));
    }
}
