using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Saldo360.Api.Workspaces;

namespace Saldo360.Tests.Api;

[Trait("Category", "CORE")]
public sealed class FinancialWriteMiddlewareTests
{
    [Theory]
    [InlineData("40001", "serialization failure")]
    [InlineData("40P01", "deadlock")]
    [InlineData("P0001", "FINANCIAL_WORKSPACE_RESET_CONFLICT")]
    public async Task ProviderWrappedConflict_IsSanitizedAndNotCached(string code, string message)
    {
        using var services = new ServiceCollection().AddOptions().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = "POST";
        context.Request.Path = "/api/financial-workspaces/synthetic/financial-reset";
        context.Response.Body = new MemoryStream();
        var error = new InvalidOperationException("Provider wrapper",
            new DbUpdateException("Update wrapper", new PostgresException(message, "ERROR", "ERROR", code)));
        await new FinancialWriteMiddleware(_ => throw error).InvokeAsync(context);
        Assert.Equal(409, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Contains("FINANCIAL_WORKSPACE_CHANGED", body);
        Assert.DoesNotContain("Provider wrapper", body);
        Assert.DoesNotContain("Update wrapper", body);
    }
}
