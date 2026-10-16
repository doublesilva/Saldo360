using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging;
using Saldo360.Api.Observability;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

public sealed class RequestObservabilityMiddlewareTests
{
    [Fact]
    public async Task RequestLog_UsesNormalizedRouteAndStructuredSafeProperties()
    {
        using var provider = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/families/family-secret/debts/debt-secret/payments";
        context.Request.QueryString = new QueryString("?token=query-secret");
        context.Request.Headers.Authorization = "Bearer token-secret";
        context.Request.Headers.Cookie = "session=cookie-secret";
        context.SetEndpoint(new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/api/families/{familyId:guid}/debts/{debtId:guid}/payments"),
            0,
            EndpointMetadataCollection.Empty,
            "payment"));
        using var activity = new Activity("request-log").Start();
        var middleware = new RequestObservabilityMiddleware(
            next: current => { current.Response.StatusCode = StatusCodes.Status409Conflict; return Task.CompletedTask; },
            factory.CreateLogger<RequestObservabilityMiddleware>());

        await middleware.InvokeAsync(context);

        var entry = Assert.Single(provider.StructuredEntries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("POST", entry.Properties["RequestMethod"]);
        Assert.Equal("/api/families/{familyId:guid}/debts/{debtId:guid}/payments", entry.Properties["RoutePattern"]);
        Assert.Equal(409, entry.Properties["StatusCode"]);
        Assert.Equal("OPERATION_CONFLICT", entry.Properties["ErrorCode"]);
        Assert.Equal(activity.TraceId.ToString(), entry.Properties["TraceId"]);
        Assert.IsType<double>(entry.Properties["ElapsedMilliseconds"]);
        Assert.DoesNotContain("query-secret", entry.Message);
        Assert.DoesNotContain("token-secret", entry.Message);
        Assert.DoesNotContain("cookie-secret", entry.Message);
        Assert.DoesNotContain("family-secret", entry.Message);
    }

    [Fact]
    public async Task SuccessfulRequest_IsInformationAndDoesNotWriteFinancialData()
    {
        using var provider = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/health/live";
        context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse("/health/live"), 0, EndpointMetadataCollection.Empty, "live"));
        var middleware = new RequestObservabilityMiddleware(_ => Task.CompletedTask, factory.CreateLogger<RequestObservabilityMiddleware>());

        await middleware.InvokeAsync(context);

        var entry = Assert.Single(provider.StructuredEntries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(200, entry.Properties["StatusCode"]);
        Assert.Equal("NONE", entry.Properties["ErrorCode"]);
    }
}
