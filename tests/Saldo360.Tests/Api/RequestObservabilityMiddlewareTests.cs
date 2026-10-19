using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Saldo360.Api.Errors;
using Saldo360.Api.Observability;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

public sealed class RequestObservabilityMiddlewareTests
{
    [Fact]
    public async Task HandledException_PreservesOriginalRouteAfterEndpointIsCleared()
    {
        using var provider = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddProvider(provider));
        // A standalone pipeline has no web host to register framework diagnostics.
        services.AddSingleton<DiagnosticListener>(_ => new DiagnosticListener("Microsoft.AspNetCore"));
        services.AddSingleton<DiagnosticSource>(sp => sp.GetRequiredService<DiagnosticListener>());
        services.AddMetrics();
        services.AddApiErrorHandling();
        await using var serviceProvider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = serviceProvider };
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/families/synthetic/recurrences";
        context.Response.Body = new MemoryStream();
        var endpoint = new RouteEndpoint(_ => Task.CompletedTask,
            RoutePatternFactory.Parse("/api/families/{familyId:guid}/recurrences"),
            0, EndpointMetadataCollection.Empty, "recurrences");
        var pipeline = new ApplicationBuilder(serviceProvider);
        pipeline.UseMiddleware<RequestObservabilityMiddleware>();
        pipeline.UseExceptionHandler();
        pipeline.Run(current =>
        {
            current.SetEndpoint(endpoint);
            throw new InvalidOperationException("Synthetic failure");
        });

        await pipeline.Build()(context);

        var entry = Assert.Single(provider.StructuredEntries, row => row.EventId.Id == 16020);
        Assert.Equal("/api/families/{familyId:guid}/recurrences", entry.Properties["RoutePattern"]);
        Assert.Equal("INTERNAL_ERROR", entry.Properties["ErrorCode"]);
        Assert.Equal(500, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Contains("INTERNAL_ERROR", body);
        Assert.DoesNotContain("Synthetic failure", body);
        Assert.DoesNotContain("InvalidOperationException", body);
    }

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

    [Fact]
    public async Task RequestWithoutActivity_UsesServerTraceIdentifier_AndIgnoresClientCorrelationValue()
    {
        using var provider = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var context = new DefaultHttpContext { TraceIdentifier = "server-trace-42" };
        context.Request.Method = HttpMethods.Get;
        context.Request.Headers["X-Correlation-Id"] = "client-controlled-secret";
        context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse("/health/live"), 0, EndpointMetadataCollection.Empty, "live"));
        var middleware = new RequestObservabilityMiddleware(_ => Task.CompletedTask, factory.CreateLogger<RequestObservabilityMiddleware>());

        await middleware.InvokeAsync(context);

        var entry = Assert.Single(provider.StructuredEntries);
        Assert.Equal("server-trace-42", entry.Properties["TraceId"]);
        Assert.DoesNotContain("client-controlled-secret", entry.Message);
        Assert.DoesNotContain(entry.Properties.Values, value => Equals(value, "client-controlled-secret"));
    }
}
