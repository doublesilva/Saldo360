using Saldo360.Api.Authentication;
using Saldo360.Application.Dashboard.PlanActual;

namespace Saldo360.Api.Dashboard;

/// <summary>
/// T10.1: Dashboard Planejado × Projetado × Realizado. A leitura não grava nada e devolve valores decimais exatos em texto; a meta de cartões é a
/// única escrita (configuração, nunca movimentação) e exige o token CSRF como as demais.
/// <list type="bullet">
/// <item><c>financial-dashboard/plan-actual?competence=yyyy-MM&amp;scope=primary|all|account&amp;accountId=</c></item>
/// <item><c>financial-dashboard/card-target</c> (PUT): meta mensal de cartões da conta pagadora, a partir de uma competência.</item>
/// </list>
/// </summary>
public static class PlanActualEndpoints
{
    public static IEndpointRouteBuilder MapPlanActualEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/financial-workspaces/{workspaceId:guid}/financial-dashboard").RequireAuthorization();
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        group.MapGet("/plan-actual", Get);
        group.MapPut("/card-target", SaveCardTarget).AddEndpointFilter<ValidateCsrfTokenFilter>();
        return app;
    }

    private static async Task<IResult> Get(Guid workspaceId, string? competence, string? scope, Guid? accountId, CurrentUser user, PlanActualService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized() : Respond(await service.GetAsync(workspaceId, userId, competence, scope, accountId, ct));

    private static async Task<IResult> SaveCardTarget(Guid workspaceId, CardTargetRequest request, CurrentUser user, PlanActualService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized() : Respond(await service.SaveCardTargetAsync(workspaceId, userId, request, ct));

    private static IResult Respond<T>(PlanActualResult<T> result) where T : class => result.Access switch
    {
        PlanActualAccess.NotFound => Results.NotFound(),
        PlanActualAccess.Invalid => Results.ValidationProblem(result.Errors!),
        _ => Results.Ok(result.Value),
    };
}
