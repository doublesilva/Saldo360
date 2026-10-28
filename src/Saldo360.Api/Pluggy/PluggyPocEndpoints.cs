using System.Text.Json.Serialization;
using Saldo360.Api.Authentication;
using Saldo360.Application.Pluggy;
using Saldo360.Domain.Pluggy;

namespace Saldo360.Api.Pluggy;

/// <summary>
/// Laboratório Pluggy (POC). Somente leitura dos dados financeiros; o único POST grava o vínculo local de um Item.
/// Não há rota para pagamentos ou transferências. Respostas nunca são armazenadas em cache.
/// </summary>
public static class PluggyPocEndpoints
{
    public static IEndpointRouteBuilder MapPluggyPocEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/financial-workspaces/{workspaceId:guid}/pluggy-poc").RequireAuthorization();
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        group.MapGet("/status", Status);
        group.MapGet("/connections", ListConnections);
        group.MapPost("/connections", AddConnection).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapGet("/connections/{id:guid}/accounts", ListAccounts);
        group.MapGet("/connections/{id:guid}/diagnostics", Diagnostics);
        group.MapGet("/accounts/{accountId}/transactions", ListTransactions);
        group.MapGet("/accounts/{accountId}/bills", ListBills);

        // T09.2: importação controlada. Habilitada por Pluggy:ImportEnabled, além de Pluggy:Enabled.
        group.MapPost("/connections/{id:guid}/sync", Sync).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapGet("/connections/{id:guid}/sync-runs", ListRuns);
        group.MapGet("/connections/{id:guid}/stability", Stability);
        group.MapGet("/imported-transactions", ListImported);
        return app;
    }

    private static async Task<IResult> Status(Guid workspaceId, CurrentUser user, PluggyPocService service, PluggyTransactionSyncService sync, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized()
        : await service.CanAccessAsync(workspaceId, userId, ct) ? Results.Ok(new { enabled = true, importEnabled = await sync.CanImportAsync(workspaceId, userId, ct) }) : Results.NotFound();

    private static async Task<IResult> ListConnections(Guid workspaceId, CurrentUser user, PluggyPocService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized()
        : Map(await service.ListConnectionsAsync(workspaceId, userId, ct), Results.Ok);

    private static async Task<IResult> AddConnection(Guid workspaceId, ConnectionRequest? request, CurrentUser user, PluggyPocService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized()
        : Map(await service.AddConnectionAsync(workspaceId, userId, request?.ItemId, request?.Label, ct),
            view => Results.Created($"/api/financial-workspaces/{workspaceId}/pluggy-poc/connections/{view.Id}/accounts", view));

    private static async Task<IResult> ListAccounts(Guid workspaceId, Guid id, CurrentUser user, PluggyPocService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized()
        : Map(await service.ListAccountsAsync(workspaceId, userId, id, ct), Results.Ok);

    private static async Task<IResult> Diagnostics(Guid workspaceId, Guid id, CurrentUser user, PluggyPocService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized()
        : Map(await service.DiagnoseAsync(workspaceId, userId, id, ct), Results.Ok);

    private static async Task<IResult> ListTransactions(Guid workspaceId, string accountId, string? from, string? to, string? after,
        CurrentUser user, PluggyPocService service, CancellationToken ct)
    {
        if (await user.GetUserIdAsync(ct) is not { } userId) return Results.Unauthorized();
        var errors = new Dictionary<string, string[]>();
        var start = IsoDateQuery.Parse(from, "from", errors);
        var end = IsoDateQuery.Parse(to, "to", errors);
        if (errors.Count > 0) return Results.ValidationProblem(errors);
        return Map(await service.ListTransactionsAsync(workspaceId, userId, accountId, start, end, after, ct), Results.Ok);
    }

    private static async Task<IResult> ListBills(Guid workspaceId, string accountId, int? page, int? pageSize,
        CurrentUser user, PluggyPocService service, CancellationToken ct)
    {
        if (await user.GetUserIdAsync(ct) is not { } userId) return Results.Unauthorized();
        if (page is < 1 || pageSize is < 1 or > 500) return Results.ValidationProblem(new Dictionary<string, string[]> { ["page"] = ["Paginação inválida."] });
        return Map(await service.ListBillsAsync(workspaceId, userId, accountId, page ?? 1, pageSize ?? 50, ct), Results.Ok);
    }

    private static async Task<IResult> Sync(Guid workspaceId, Guid id, CurrentUser user, PluggyTransactionSyncService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized() : Map(await service.SyncAsync(workspaceId, userId, id, ct), Results.Ok);

    private static async Task<IResult> ListRuns(Guid workspaceId, Guid id, CurrentUser user, PluggyTransactionSyncService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized() : Map(await service.ListRunsAsync(workspaceId, userId, id, ct), Results.Ok);

    private static async Task<IResult> Stability(Guid workspaceId, Guid id, CurrentUser user, PluggyTransactionSyncService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized() : Map(await service.StabilityAsync(workspaceId, userId, id, ct), Results.Ok);

    private static async Task<IResult> ListImported(Guid workspaceId, Guid? connectionId, string? accountId, string? from, string? to, string? status, string? classification,
        int? page, int? pageSize, CurrentUser user, PluggyTransactionSyncService service, CancellationToken ct)
    {
        if (await user.GetUserIdAsync(ct) is not { } userId) return Results.Unauthorized();
        var errors = new Dictionary<string, string[]>();
        var start = IsoDateQuery.Parse(from, "from", errors);
        var end = IsoDateQuery.Parse(to, "to", errors);
        PluggyTransactionStatus? parsedStatus = null;
        if (!string.IsNullOrEmpty(status))
        {
            if (Enum.TryParse<PluggyTransactionStatus>(status, false, out var s) && Enum.IsDefined(s)) parsedStatus = s; else errors["status"] = ["Status inválido."];
        }
        PluggyTransactionClass? parsedClass = null;
        if (!string.IsNullOrEmpty(classification))
        {
            if (Enum.TryParse<PluggyTransactionClass>(classification, false, out var c) && Enum.IsDefined(c)) parsedClass = c; else errors["classification"] = ["Classificação inválida."];
        }
        if (page is < 1 || pageSize is < 1 or > PluggyTransactionSyncService.MaxPageSize) errors["page"] = ["Paginação inválida."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);
        return Map(await service.ListAsync(workspaceId, userId, new(connectionId, accountId, start, end, parsedStatus, parsedClass, page ?? 1, pageSize ?? 50), ct), Results.Ok);
    }

    private static IResult Map<T>(PluggyResult<T> result, Func<T, IResult> ok) => result.Access switch
    {
        PluggyAccess.Allowed => ok(result.Value!),
        PluggyAccess.NotFound => Results.NotFound(),
        PluggyAccess.Invalid => Results.ValidationProblem(new Dictionary<string, string[]> { ["pluggy"] = [result.Message ?? "Requisição inválida."] }),
        PluggyAccess.Conflict => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: result.Message),
        _ => IntegrationFailure(result),
    };

    /// <summary>Falhas da Pluggy viram erros controlados, com código estável e sem detalhes do provedor.</summary>
    private static IResult IntegrationFailure<T>(PluggyResult<T> result)
    {
        var (status, code) = result.Failure switch
        {
            PluggyFailureKind.NotConfigured => (StatusCodes.Status503ServiceUnavailable, "PLUGGY_NOT_CONFIGURED"),
            PluggyFailureKind.RateLimited => (StatusCodes.Status429TooManyRequests, "PLUGGY_RATE_LIMITED"),
            PluggyFailureKind.Timeout => (StatusCodes.Status504GatewayTimeout, "PLUGGY_TIMEOUT"),
            PluggyFailureKind.Authentication => (StatusCodes.Status502BadGateway, "PLUGGY_AUTHENTICATION_FAILED"),
            PluggyFailureKind.Forbidden => (StatusCodes.Status502BadGateway, "PLUGGY_ACCESS_DENIED"),
            PluggyFailureKind.Rejected => (StatusCodes.Status502BadGateway, "PLUGGY_REQUEST_REJECTED"),
            PluggyFailureKind.InvalidResponse => (StatusCodes.Status502BadGateway, "PLUGGY_INVALID_RESPONSE"),
            _ => (StatusCodes.Status503ServiceUnavailable, "PLUGGY_UNAVAILABLE"),
        };
        return Results.Problem(statusCode: status, title: result.Message, extensions: new Dictionary<string, object?> { ["code"] = code });
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConnectionRequest(string? ItemId, string? Label);
