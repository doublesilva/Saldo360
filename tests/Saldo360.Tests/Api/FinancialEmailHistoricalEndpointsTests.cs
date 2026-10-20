using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

public sealed class FinancialEmailHistoricalEndpointsTests : ApiTestBase
{
    private const string Url = "/api/financial-email-import/Unknown/historical-scan";
    private Task<HttpResponseMessage> Post(string? cookie, string? token, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Url) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        if (token is not null) request.Headers.Add("X-CSRF-TOKEN", token);
        return Client.SendAsync(request);
    }

    [Fact]
    public async Task HistoricalRequiresAuthenticationCsrfAndExplicitValidWindowWithoutWritingState()
    {
        var valid = new { dryRun = true, from = "2026-10-01", through = "2026-10-31" };
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(null, null, valid)).StatusCode);
        var session = await SignInAsync("historical-operator");
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(session, null, valid)).StatusCode);
        var (cookie, token) = await GetCsrfAsync(session); var cookies = session + "; " + cookie;
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(cookies, token, new { dryRun = true, through = "2026-10-31" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(cookies, token, new { dryRun = true, from = "2026-10-01" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(cookies, token, new { from = "2026-10-01", through = "2026-10-31" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(cookies, token, new { dryRun = true, from = "2026-11-01", through = "2026-10-31" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Post(cookies, token, valid)).StatusCode);
        await WithDbAsync(async db =>
        {
            Assert.False(await db.FinancialEmailCollectorCheckpoints.AnyAsync());
            Assert.False(await db.FinancialDocumentInboxItems.AnyAsync());
            Assert.False(await db.FinancialTransactions.AnyAsync());
            Assert.False(await db.FinancialPlannedTransactions.AnyAsync());
        });
    }
}
