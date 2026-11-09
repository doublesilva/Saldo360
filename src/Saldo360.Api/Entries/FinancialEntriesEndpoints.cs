using Saldo360.Api.Authentication;
using Saldo360.Application.Entries;

namespace Saldo360.Api.Entries;

/// <summary>
/// T13.1: <c>GET financial-entries</c>: todos os movimentos de uma competência numa só lista paginada (transações, previsões, recorrências, dívidas, transferências e cartões), uma linha por movimento
/// lógico. Filtros, ordenação e paginação são aplicados no servidor. Somente leitura; nenhum dado é gravado ou duplicado.
/// </summary>
public static class FinancialEntriesEndpoints
{
    public static IEndpointRouteBuilder MapFinancialEntriesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/financial-workspaces/{workspaceId:guid}/financial-entries").RequireAuthorization();
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        group.MapGet("", List);
        return app;
    }

    private static async Task<IResult> List(Guid workspaceId, string? competence, string? scope, Guid? accountId, Guid? categoryId, string? type, string? status, string? source, string? nature, string? search,
        bool? includeCancelled, string? sort, string? direction, int? page, int? pageSize, Guid? cardId, bool? withoutCard, string? groupBy, CurrentUser user, FinancialEntriesQueryService service, CancellationToken ct)
    {
        if (await user.GetUserIdAsync(ct) is not { } userId) return Results.Unauthorized();
        var result = await service.ListAsync(workspaceId, userId, new(competence, scope, accountId, categoryId, type, status, source, nature, search, includeCancelled ?? false, sort, direction, page, pageSize, cardId, withoutCard ?? false, groupBy), ct);
        return result.NotFound ? Results.NotFound() : result.Errors is not null ? Results.ValidationProblem(result.Errors) : Results.Ok(result.Page);
    }
}
