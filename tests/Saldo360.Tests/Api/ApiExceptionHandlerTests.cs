using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Saldo360.Api.Errors;

namespace Saldo360.Tests.Api;

public sealed class ApiExceptionHandlerTests
{
    [Fact]
    public async Task UnexpectedError_IsSanitizedAndCorrelated()
    {
        var writer = new CapturingProblemDetailsService();
        var handler = new ApiExceptionHandler(writer, NullLogger<ApiExceptionHandler>.Instance);
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
