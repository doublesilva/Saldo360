using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Saldo360.Domain.Common;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

[Collection(PostgresCollection.Name)]
public sealed class FinancialDashboardEndpointsTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
    [Fact]
    public async Task EmptyFamily_ReturnsAllSectionsWithSixMonthHorizon()
    {
        var owner = await SignInAsync("dashboard-owner");
        var family = await CreateWorkspaceAsync(owner, "Dashboard");

        var response = await SendAsync(HttpMethod.Get, $"/api/financial-workspaces/{family.Id}/financial-dashboard", owner);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(6, body.RootElement.GetProperty("months").GetInt32());
        Assert.True(body.RootElement.TryGetProperty("position", out _));
        Assert.True(body.RootElement.TryGetProperty("cashFlow", out _));
        Assert.True(body.RootElement.TryGetProperty("obligations", out _));
        Assert.True(body.RootElement.TryGetProperty("budgets", out _));
        Assert.True(body.RootElement.TryGetProperty("creditCards", out _));
        Assert.True(body.RootElement.TryGetProperty("debts", out _));
        Assert.True(body.RootElement.TryGetProperty("goals", out _));
        Assert.Equal(6, body.RootElement.GetProperty("monthlyProjection").GetArrayLength());
        Assert.False(body.RootElement.GetProperty("coverage").GetProperty("complete").GetBoolean());
    }

    [Fact]
    public async Task ValidatesHorizonAndCompetence_AndIsolatesFamily()
    {
        var owner = await SignInAsync("dashboard-owner-2");
        var outsider = await SignInAsync("dashboard-outsider-2");
        var family = await CreateWorkspaceAsync(owner, "Dashboard 2");

        var invalidMonths = await SendAsync(HttpMethod.Get, $"/api/financial-workspaces/{family.Id}/financial-dashboard?months=13", owner);
        var invalidCompetence = await SendAsync(HttpMethod.Get, $"/api/financial-workspaces/{family.Id}/financial-dashboard?competence=2026-1", owner);
        var forbiddenFamily = await SendAsync(HttpMethod.Get, $"/api/financial-workspaces/{family.Id}/financial-dashboard", outsider);

        Assert.Equal(HttpStatusCode.BadRequest, invalidMonths.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidCompetence.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, forbiddenFamily.StatusCode);
    }

    [Fact]
    public async Task NegativeAccount_ProducesStructuredAlertWithoutChangingFinancialData()
    {
        var owner = await SignInAsync("dashboard-negative");
        var family = await CreateWorkspaceAsync(owner, "Dashboard negative");
        var csrf = await GetCsrfAsync(owner);
        var accountRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/financial-workspaces/{family.Id}/accounts")
        {
            Content = JsonContent.Create(new { name = "Conta negativa", type = "Checking", financialScope = "PF", openingBalance = "-100.00", openingBalanceDate = BusinessCalendar.Today(DateTimeOffset.UtcNow).AddDays(-1) })
        };
        accountRequest.Headers.Add("Cookie", $"{owner}; {csrf.Cookie}");
        accountRequest.Headers.Add("X-CSRF-TOKEN", csrf.Token);
        var created = await Client.SendAsync(accountRequest);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var response = await SendAsync(HttpMethod.Get, $"/api/financial-workspaces/{family.Id}/financial-dashboard?months=1", owner);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var alert = body.RootElement.GetProperty("alerts").EnumerateArray().Single(x => x.GetProperty("code").GetString() == "negative-realized-balance");
        Assert.Equal("Critical", alert.GetProperty("severity").GetString());
        Assert.Equal("100.00", alert.GetProperty("amount").GetString());
    }
}
