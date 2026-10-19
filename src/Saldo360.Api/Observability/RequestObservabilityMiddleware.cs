using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Saldo360.Api.Errors;

namespace Saldo360.Api.Observability;

public sealed class RequestObservabilityMiddleware(
    RequestDelegate next,
    ILogger<RequestObservabilityMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        await next(context);

        // ExceptionHandler clears the current endpoint but preserves the original in its feature.
        var endpoint = context.GetEndpoint() ?? context.Features.Get<IExceptionHandlerFeature>()?.Endpoint;
        var route = endpoint is RouteEndpoint routeEndpoint
            ? routeEndpoint.RoutePattern.RawText
            : "unmatched";
        var status = context.Response.StatusCode;
        var errorCode = ErrorCode(status);
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var traceId = ApiErrorHandling.CurrentTraceId(context);
        var level = Level(status, context.Request.Method, route);

        logger.Log(
            level,
            new EventId(16020, "RequestCompleted"),
            "HTTP request completed. RequestMethod: {RequestMethod}; RoutePattern: {RoutePattern}; StatusCode: {StatusCode}; ElapsedMilliseconds: {ElapsedMilliseconds}; ErrorCode: {ErrorCode}; TraceId: {TraceId}",
            context.Request.Method,
            route,
            status,
            elapsed,
            errorCode,
            traceId);
    }

    private static LogLevel Level(int status, string method, string? route)
    {
        if (status >= 500 || status is 401 or 403 or 409 or 422) return LogLevel.Warning;
        if (status < 400 && HttpMethods.IsGet(method) && route?.Contains("/dashboard", StringComparison.OrdinalIgnoreCase) == true)
            return LogLevel.Debug;
        return LogLevel.Information;
    }

    private static string ErrorCode(int status) => status switch
    {
        < 400 => "NONE",
        400 => "VALIDATION_ERROR",
        401 => "AUTHENTICATION_REQUIRED",
        403 => "ACCESS_DENIED",
        404 => "RESOURCE_NOT_FOUND",
        409 => "OPERATION_CONFLICT",
        422 => "BUSINESS_RULE_VIOLATION",
        500 => "INTERNAL_ERROR",
        503 => "DEPENDENCY_UNAVAILABLE",
        _ => "HTTP_ERROR",
    };
}
