using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Saldo360.Api.Errors;
using Saldo360.Tests.Support;

namespace Saldo360.Tests.Api;

public sealed class ApiExceptionHandlerTests
{
    [Fact]
    public async Task UnexpectedError_IsSanitizedAndCorrelated()
    {
        var writer = new CapturingProblemDetailsService();
        using var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var handler = new ApiExceptionHandler(writer, loggerFactory.CreateLogger<ApiExceptionHandler>());
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/private-operation";
        using var activity = new Activity("problem-test").Start();

        var handled = await handler.TryHandleAsync(context, new InvalidOperationException("Host=secret; SELECT password"), CancellationToken.None);

        Assert.True(handled);
        var problem = writer.Problem!;
        Assert.Equal(500, problem.Status);
        Assert.Equal("INTERNAL_ERROR", problem.Extensions["code"]);
        Assert.Equal(activity.TraceId.ToString(), problem.Extensions["traceId"]);
        Assert.DoesNotContain("secret", problem.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT", problem.Detail, StringComparison.OrdinalIgnoreCase);
        var log = Assert.Single(logs.StructuredEntries, entry => entry.Level == LogLevel.Error);
        Assert.Equal(activity.TraceId.ToString(), log.Properties["TraceId"]);
        Assert.Equal(typeof(InvalidOperationException).FullName, log.Properties["ExceptionType"]);
        Assert.Equal("INTERNAL_ERROR", log.Properties["ErrorCode"]);
        Assert.Equal("None", log.Properties["Dependency"]);
        Assert.Null(log.Exception);
        Assert.DoesNotContain("secret", log.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT", log.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DatabaseTimeout_IsSanitizedAsDependencyUnavailable()
    {
        var writer = new CapturingProblemDetailsService();
        using var logs = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
        var handler = new ApiExceptionHandler(writer, loggerFactory.CreateLogger<ApiExceptionHandler>());
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/financial-write";

        var handled = await handler.TryHandleAsync(
            context,
            new Npgsql.ObservabilityTestException("Host=private;Password=secret; command with amount 999.99"),
            CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(503, writer.Problem!.Status);
        Assert.Equal("DEPENDENCY_UNAVAILABLE", writer.Problem.Extensions["code"]);
        Assert.DoesNotContain("secret", writer.Problem.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("999.99", writer.Problem.Detail, StringComparison.OrdinalIgnoreCase);
        var log = Assert.Single(logs.StructuredEntries, entry => entry.Level == LogLevel.Error);
        Assert.Equal("PostgreSQL", log.Properties["Dependency"]);
        Assert.Equal("DEPENDENCY_UNAVAILABLE", log.Properties["ErrorCode"]);
        Assert.Null(log.Exception);
        Assert.DoesNotContain("secret", log.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("999.99", log.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CapturingProblemDetailsService : IProblemDetailsService
    {
        public ProblemDetails? Problem { get; private set; }
        public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context)
        {
            Problem = context.ProblemDetails;
            return ValueTask.FromResult(true);
        }

        public ValueTask WriteAsync(ProblemDetailsContext context)
        {
            Problem = context.ProblemDetails;
            return ValueTask.CompletedTask;
        }
    }
}
