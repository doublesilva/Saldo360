using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Saldo360.Api.Errors;

public static class ApiErrorHandling
{
    public static IServiceCollection AddApiErrorHandling(this IServiceCollection services)
    {
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            var problem = context.ProblemDetails;
            var status = problem.Status ?? context.HttpContext.Response.StatusCode;
            var code = problem.Extensions.TryGetValue("code", out var configuredCode)
                ? configuredCode?.ToString()
                : DefaultCode(status, problem);

            problem.Status = status;
            problem.Title ??= DefaultTitle(status);
            problem.Detail ??= DefaultDetail(status);
            problem.Instance ??= context.HttpContext.Request.Path;
            problem.Type ??= $"https://saldo360.local/problems/{code!.ToLowerInvariant().Replace('_', '-')}";
            problem.Extensions["code"] = code;
            problem.Extensions["traceId"] = CurrentTraceId(context.HttpContext);
        });
        services.AddExceptionHandler<ApiExceptionHandler>();
        return services;
    }

    internal static string CurrentTraceId(HttpContext context) =>
        Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;

    private static string DefaultCode(int status, ProblemDetails problem) => status switch
    {
        StatusCodes.Status400BadRequest when problem is HttpValidationProblemDetails => "VALIDATION_ERROR",
        StatusCodes.Status400BadRequest => "INVALID_REQUEST",
        StatusCodes.Status401Unauthorized => "AUTHENTICATION_REQUIRED",
        StatusCodes.Status403Forbidden => "ACCESS_DENIED",
        StatusCodes.Status404NotFound => "RESOURCE_NOT_FOUND",
        StatusCodes.Status409Conflict => "OPERATION_CONFLICT",
        StatusCodes.Status422UnprocessableEntity => "BUSINESS_RULE_VIOLATION",
        StatusCodes.Status503ServiceUnavailable => "DEPENDENCY_UNAVAILABLE",
        StatusCodes.Status500InternalServerError => "INTERNAL_ERROR",
        _ => "HTTP_ERROR",
    };

    private static string DefaultTitle(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "Dados inválidos.",
        StatusCodes.Status401Unauthorized => "Autenticação necessária.",
        StatusCodes.Status403Forbidden => "Acesso negado.",
        StatusCodes.Status404NotFound => "Recurso não encontrado.",
        StatusCodes.Status409Conflict => "Conflito na operação.",
        StatusCodes.Status422UnprocessableEntity => "Operação não permitida.",
        StatusCodes.Status503ServiceUnavailable => "Serviço temporariamente indisponível.",
        StatusCodes.Status500InternalServerError => "Falha inesperada.",
        _ => "Falha na requisição.",
    };

    private static string DefaultDetail(int status) => status switch
    {
        StatusCodes.Status401Unauthorized => "Entre novamente para continuar.",
        StatusCodes.Status403Forbidden => "Você não possui permissão para executar esta operação.",
        StatusCodes.Status404NotFound => "O recurso solicitado não existe ou não está disponível.",
        StatusCodes.Status500InternalServerError => "Não foi possível concluir a operação. Use o identificador de diagnóstico ao solicitar suporte.",
        StatusCodes.Status503ServiceUnavailable => "Tente novamente mais tarde.",
        _ => "Revise os dados e tente novamente.",
    };
}

public sealed class ApiExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var traceId = ApiErrorHandling.CurrentTraceId(httpContext);
        var dependencyUnavailable = IsDatabaseDependencyUnavailable(exception);
        var status = dependencyUnavailable
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status500InternalServerError;
        var code = dependencyUnavailable ? "DEPENDENCY_UNAVAILABLE" : "INTERNAL_ERROR";
        var dependency = dependencyUnavailable ? "PostgreSQL" : "None";
        logger.LogError(
            "Unhandled API failure. ExceptionType: {ExceptionType}; ErrorCode: {ErrorCode}; Dependency: {Dependency}; TraceId: {TraceId}; Path: {Path}",
            exception.GetType().FullName,
            code,
            dependency,
            traceId,
            httpContext.Request.Path);

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = dependencyUnavailable ? "Serviço temporariamente indisponível." : "Falha inesperada.",
                Detail = dependencyUnavailable
                    ? "Uma dependência necessária está indisponível. Tente novamente mais tarde."
                    : "Não foi possível concluir a operação. Use o identificador de diagnóstico ao solicitar suporte.",
                Instance = httpContext.Request.Path,
                Type = $"https://saldo360.local/problems/{code.ToLowerInvariant().Replace('_', '-')}",
                Extensions =
                {
                    ["code"] = code,
                    ["traceId"] = traceId,
                },
            },
        });
    }

    private static bool IsDatabaseDependencyUnavailable(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            var type = current.GetType();
            if (type.Namespace?.StartsWith("Npgsql", StringComparison.Ordinal) == true) return true;
        }
        return false;
    }
}

public static class ApiProblems
{
    public static IResult Validation(IDictionary<string, string[]> errors) => Results.ValidationProblem(
        errors,
        title: "Dados inválidos.",
        statusCode: StatusCodes.Status400BadRequest,
        extensions: new Dictionary<string, object?> { ["code"] = "VALIDATION_ERROR" });

    public static IResult Unauthorized() => Problem(
        StatusCodes.Status401Unauthorized,
        "Autenticação necessária.",
        "Entre novamente para continuar.",
        "AUTHENTICATION_REQUIRED");

    public static IResult NotFound() => Problem(
        StatusCodes.Status404NotFound,
        "Recurso não encontrado.",
        "O recurso solicitado não existe ou não está disponível.",
        "RESOURCE_NOT_FOUND");

    public static IResult Conflict(string? detail, string code = "OPERATION_CONFLICT") => Problem(
        StatusCodes.Status409Conflict,
        "Conflito na operação.",
        detail ?? "O estado atual não permite concluir esta operação.",
        code);

    private static IResult Problem(int status, string title, string detail, string code) => Results.Problem(
        statusCode: status,
        title: title,
        detail: detail,
        extensions: new Dictionary<string, object?> { ["code"] = code });
}
