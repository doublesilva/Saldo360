using Saldo360.Api.Authentication;
using Saldo360.Application.Dashboard.Overview;
using Saldo360.Application.Dashboard.PlanActual;
using Saldo360.Application.Dashboard;

namespace Saldo360.Api.Dashboard;

/// <summary>
/// T10.1: Dashboard Planejado × Projetado × Realizado. A leitura não grava nada e devolve valores decimais exatos em texto; a meta de cartões é a
/// única escrita (configuração, nunca movimentação) e exige o token CSRF como as demais.
/// <list type="bullet">
/// <item><c>financial-dashboard/plan-actual?competence=yyyy-MM&amp;scope=primary|all|account&amp;accountId=</c></item>
/// <item><c>financial-dashboard/overview?competence=yyyy-MM&amp;scope=primary|all|account&amp;accountId=</c> (R01): contas com saldo, compromissos da Agenda e metas.</item>
/// <item><c>financial-dashboard/expense-distribution?competence=yyyy-MM&amp;scope=primary|all|account&amp;accountId=</c>: todas as categorias com gasto ("Ver todas" do dashboard).</item>
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
        group.MapGet("/overview", Overview);
        group.MapGet("/expense-distribution", ExpenseDistribution);
        group.MapGet("/budget-cards", BudgetCards);
        group.MapGet("/budget-categories", BudgetCategories);
        group.MapGet("/overdraft-recovery/{accountId:guid}", Recovery);
        group.MapPost("/overdraft-recovery/{accountId:guid}", CaptureRecovery).AddEndpointFilter<ValidateCsrfTokenFilter>();
        return app;
    }

    private static async Task<IResult> Get(Guid workspaceId, string? competence, string? scope, Guid? accountId, CurrentUser user, PlanActualService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized() : Respond(await service.GetAsync(workspaceId, userId, competence, scope, accountId, ct));

    private static async Task<IResult> Overview(Guid workspaceId, string? competence, string? scope, Guid? accountId, CurrentUser user, DashboardOverviewService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized() : Respond(await service.GetAsync(workspaceId, userId, competence, scope, accountId, ct));

    private static async Task<IResult> ExpenseDistribution(Guid workspaceId, string? competence, string? scope, Guid? accountId, CurrentUser user, DashboardOverviewService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized() : Respond(await service.GetExpenseDistributionAsync(workspaceId, userId, competence, scope, accountId, ct));

    private static async Task<IResult> BudgetCards(Guid workspaceId, string? competence, CurrentUser user, DashboardOverviewService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized() : Respond(await service.GetBudgetCardsAsync(workspaceId, userId, competence, ct));

    private static async Task<IResult> BudgetCategories(Guid workspaceId, string? competence, CurrentUser user, DashboardOverviewService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized() : Respond(await service.GetBudgetCategoriesAsync(workspaceId, userId, competence, ct));

    private static async Task<IResult> Recovery(Guid workspaceId, Guid accountId, string? competence, CurrentUser user, OverdraftRecoveryService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized() : Respond(await service.GetAsync(workspaceId, userId, accountId, competence ?? string.Empty, ct));

    private static async Task<IResult> CaptureRecovery(Guid workspaceId, Guid accountId, RecoveryRequest? request, CurrentUser user, OverdraftRecoveryService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized() : Respond(await service.CaptureAsync(workspaceId, userId, accountId, request?.Competence ?? string.Empty, ct));

    private static IResult Respond<T>(PlanActualResult<T> result) where T : class => result.Access switch
    {
        PlanActualAccess.NotFound => Results.NotFound(),
        PlanActualAccess.Invalid => Results.ValidationProblem(result.Errors!),
        _ => Results.Ok(result.Value),
    };
}

public sealed record RecoveryRequest(string? Competence);
