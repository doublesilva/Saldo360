using System.Diagnostics;
using Saldo360.Api.Errors;

namespace Saldo360.Api.Observability;

public sealed class FinancialOperationLogger(ILogger<FinancialOperationLogger> logger)
{
    public long Start() => Stopwatch.GetTimestamp();

    public void Completed(HttpContext context, long started, string operationType, string result, string errorCode)
    {
        var level = errorCode is "NONE" or "VALIDATION_ERROR" or "RESOURCE_NOT_FOUND"
            ? LogLevel.Information
            : LogLevel.Warning;
        logger.Log(
            level,
            new EventId(16021, "FinancialOperationCompleted"),
            "Financial operation completed. OperationType: {OperationType}; OperationResult: {OperationResult}; ErrorCode: {ErrorCode}; ElapsedMilliseconds: {ElapsedMilliseconds}; TraceId: {TraceId}",
            operationType,
            result,
            errorCode,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            ApiErrorHandling.CurrentTraceId(context));
    }
}
