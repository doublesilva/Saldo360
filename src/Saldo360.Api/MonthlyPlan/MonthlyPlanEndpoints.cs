using Saldo360.Api.Authentication;
using Saldo360.Application.MonthlyPlan;

namespace Saldo360.Api.MonthlyPlan;

/// <summary>
/// T10.2: planejamento mensal do orçamento. As leituras não gravam e devolvem decimais exatos em texto; as escritas são configuração do orçamento (nunca
/// movimentação) e exigem o token CSRF. Participação obrigatória: orçamento de outro usuário recebe 404.
/// <list type="bullet">
/// <item><c>GET monthly-plan?competence=yyyy-MM&amp;scope=primary|all|account&amp;accountId=</c>: o mês, com a realização do Dashboard.</item>
/// <item><c>GET monthly-plan/six-months?from=yyyy-MM&amp;scope=&amp;accountId=</c>: o mês e os cinco seguintes.</item>
/// <item><c>POST|PUT|DELETE monthly-plan/lines[/{id}]</c>: receitas, despesas e transferências previstas.</item>
/// <item><c>PUT|DELETE monthly-plan/category-limits</c>: limite mensal por categoria.</item>
/// <item><c>GET monthly-plan/history?competence=&amp;subjectId=</c>, <c>POST monthly-plan/copy</c>.</item>
/// <item><c>GET monthly-plan/working-days</c>, <c>GET monthly-plan/hourly-income</c>, <c>POST|DELETE monthly-plan/non-working-days</c>.</item>
/// </list>
/// </summary>
public static class MonthlyPlanEndpoints
{
    public static IEndpointRouteBuilder MapMonthlyPlanEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/financial-workspaces/{workspaceId:guid}/monthly-plan").RequireAuthorization();
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        group.MapGet("", Get);
        group.MapGet("/six-months", SixMonths);
        group.MapGet("/history", History);
        group.MapGet("/working-days", WorkingDays);
        group.MapGet("/hourly-income", HourlyIncome);
        group.MapPost("/lines", CreateLine).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapPut("/lines/{lineId:guid}", ReviseLine).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapDelete("/lines/{lineId:guid}", RemoveLine).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapPut("/category-limits", SetLimit).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapDelete("/category-limits/{categoryId:guid}", RemoveLimit).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapPost("/copy", Copy).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapPost("/non-working-days", AddNonWorkingDay).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapDelete("/non-working-days/{id:guid}", RemoveNonWorkingDay).AddEndpointFilter<ValidateCsrfTokenFilter>();
        return app;
    }

    private static async Task<IResult> Run<T>(CurrentUser user, CancellationToken ct, Func<Guid, Task<MonthlyPlanResult<T>>> call, Func<T, IResult>? ok = null) where T : class =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized() : Respond(await call(userId), ok);

    private static IResult Respond<T>(MonthlyPlanResult<T> result, Func<T, IResult>? ok = null) where T : class => result.Access switch
    {
        MonthlyPlanAccess.NotFound => Results.NotFound(),
        MonthlyPlanAccess.Invalid => Results.ValidationProblem(result.Errors!),
        MonthlyPlanAccess.Conflict => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: result.Message),
        _ => ok is null ? Results.Ok(result.Value) : ok(result.Value!),
    };

    private static Task<IResult> Get(Guid workspaceId, string? competence, string? scope, Guid? accountId, CurrentUser user, MonthlyPlanService service, CancellationToken ct) =>
        Run(user, ct, id => service.GetAsync(workspaceId, id, competence, scope, accountId, ct));

    private static Task<IResult> SixMonths(Guid workspaceId, string? from, string? scope, Guid? accountId, CurrentUser user, MonthlyPlanService service, CancellationToken ct) =>
        Run(user, ct, id => service.SixMonthsAsync(workspaceId, id, from, scope, accountId, ct));

    private static Task<IResult> History(Guid workspaceId, string? competence, Guid? subjectId, CurrentUser user, MonthlyPlanService service, CancellationToken ct) =>
        Run(user, ct, id => service.HistoryAsync(workspaceId, id, competence, subjectId, ct));

    private static Task<IResult> WorkingDays(Guid workspaceId, string? competence, CurrentUser user, MonthlyPlanService service, CancellationToken ct) =>
        Run(user, ct, id => service.WorkingDaysAsync(workspaceId, id, competence, ct));

    private static Task<IResult> HourlyIncome(Guid workspaceId, string? competence, string? hourlyRate, string? hoursPerDay, int? billableDays, CurrentUser user, MonthlyPlanService service, CancellationToken ct) =>
        Run(user, ct, id => service.HourlyIncomeAsync(workspaceId, id, competence, hourlyRate, hoursPerDay, billableDays, ct));

    private static Task<IResult> CreateLine(Guid workspaceId, PlanLineRequest request, CurrentUser user, MonthlyPlanService service, CancellationToken ct) =>
        Run(user, ct, id => service.CreateLineAsync(workspaceId, id, request, ct), line => Results.Created($"/api/financial-workspaces/{workspaceId}/monthly-plan?competence={line.Competence}", line));

    private static Task<IResult> ReviseLine(Guid workspaceId, Guid lineId, PlanLineRequest request, CurrentUser user, MonthlyPlanService service, CancellationToken ct) =>
        Run(user, ct, id => service.ReviseLineAsync(workspaceId, id, lineId, request, ct));

    private static Task<IResult> RemoveLine(Guid workspaceId, Guid lineId, string? note, CurrentUser user, MonthlyPlanService service, CancellationToken ct) =>
        Run(user, ct, id => service.RemoveLineAsync(workspaceId, id, lineId, note, ct));

    private static Task<IResult> SetLimit(Guid workspaceId, CategoryLimitRequest request, CurrentUser user, MonthlyPlanService service, CancellationToken ct) =>
        Run(user, ct, id => service.SetCategoryLimitAsync(workspaceId, id, request, ct));

    private static Task<IResult> RemoveLimit(Guid workspaceId, Guid categoryId, string? competence, string? note, CurrentUser user, MonthlyPlanService service, CancellationToken ct) =>
        Run(user, ct, id => service.RemoveCategoryLimitAsync(workspaceId, id, competence, categoryId, note, ct));

    private static Task<IResult> Copy(Guid workspaceId, CopyPlanRequest request, CurrentUser user, MonthlyPlanService service, CancellationToken ct) =>
        Run(user, ct, id => service.CopyAsync(workspaceId, id, request, ct));

    private static Task<IResult> AddNonWorkingDay(Guid workspaceId, NonWorkingDayRequest request, CurrentUser user, MonthlyPlanService service, CancellationToken ct) =>
        Run(user, ct, id => service.AddNonWorkingDayAsync(workspaceId, id, request, ct), day => Results.Created($"/api/financial-workspaces/{workspaceId}/monthly-plan/working-days", day));

    private static Task<IResult> RemoveNonWorkingDay(Guid workspaceId, Guid id, string? competence, CurrentUser user, MonthlyPlanService service, CancellationToken ct) =>
        Run(user, ct, userId => service.RemoveNonWorkingDayAsync(workspaceId, userId, id, competence, ct));
}
