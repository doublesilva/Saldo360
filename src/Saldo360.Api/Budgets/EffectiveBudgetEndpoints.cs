using System.Text.Json.Serialization;
using Saldo360.Api.Authentication;
using Saldo360.Application.Budgets;

namespace Saldo360.Api.Budgets;

/// <summary>
/// T12.1: orçamento mensal recorrente. Todas as leituras são somente leitura (nenhum orçamento mensal é criado ao consultar). As escritas acrescentam ao log de regras:
/// <c>POST effective/changes</c> (somente este mês ou este mês e os próximos) e <c>POST effective/reference</c> (usar um mês existente como referência do padrão).
/// </summary>
public static class EffectiveBudgetEndpoints
{
    public static RouteGroupBuilder MapEffectiveBudget(this RouteGroupBuilder budgets)
    {
        budgets.MapGet("/effective", Get);
        budgets.MapGet("/effective/execution", GetExecution);
        budgets.MapGet("/effective/targets/{targetId:guid}/composition", GetComposition);
        budgets.MapGet("/effective/history", History);
        budgets.MapGet("/effective/reference", ReferencePreview);
        budgets.MapPost("/effective/changes", Change).AddEndpointFilter<ValidateCsrfTokenFilter>();
        budgets.MapPost("/effective/reference", ApplyReference).AddEndpointFilter<ValidateCsrfTokenFilter>();
        return budgets;
    }

    private static IResult ToResponse<T>(EffectiveResult<T> result) where T : class => result.Access switch
    {
        EffectiveAccess.NotFound => Results.NotFound(),
        EffectiveAccess.Invalid => Results.ValidationProblem(result.Errors!),
        EffectiveAccess.Conflict => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: result.Message),
        _ => Results.Ok(result.Value),
    };

    private static async Task<IResult> Get(Guid workspaceId, string? competence, CurrentUser currentUser, EffectiveBudgetService service, CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        return ToResponse(await service.GetAsync(workspaceId, userId, competence, cancellationToken));
    }

    private static async Task<IResult> GetExecution(Guid workspaceId, string? competence, CurrentUser currentUser, FinancialBudgetExecutionService service, CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        if (competence is null || !FinancialBudgetService.TryParseCompetence(competence, out var month))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["competence"] = ["Use uma competência válida no formato yyyy-MM."] });
        var result = await service.GetByCompetenceAsync(workspaceId, month, userId, cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(FinancialBudgetExecutionResponse.From(result));
    }

    private static async Task<IResult> GetComposition(Guid workspaceId, Guid targetId, string? competence, int? page, int? pageSize, CurrentUser currentUser,
        FinancialBudgetExecutionService service, CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        if (competence is null || !FinancialBudgetService.TryParseCompetence(competence, out var month))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["competence"] = ["Use uma competência válida no formato yyyy-MM."] });
        var result = await service.GetEffectiveCompositionAsync(workspaceId, month, targetId, userId, page, pageSize, cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(BudgetCompositionResponse.From(result));
    }

    private static async Task<IResult> History(Guid workspaceId, string? competence, CurrentUser currentUser, EffectiveBudgetService service, CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        return ToResponse(await service.HistoryAsync(workspaceId, userId, competence, cancellationToken));
    }

    private static async Task<IResult> ReferencePreview(Guid workspaceId, string? source, string? from, CurrentUser currentUser, EffectiveBudgetService service, CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        return ToResponse(await service.ReferencePreviewAsync(workspaceId, userId, source, from, cancellationToken));
    }

    private static async Task<IResult> Change(Guid workspaceId, ChangeBudgetRuleRequest? request, CurrentUser currentUser, EffectiveBudgetService service, CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        return ToResponse(await service.ChangeAsync(workspaceId, userId,
            request is null ? null : new(request.Competence, request.Target, request.CategoryId, request.Mode, request.Action, request.Amount, request.Note), cancellationToken));
    }

    private static async Task<IResult> ApplyReference(Guid workspaceId, ApplyBudgetReferenceRequest? request, CurrentUser currentUser, EffectiveBudgetService service, CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        return ToResponse(await service.ApplyReferenceAsync(workspaceId, userId,
            request is null ? null : new(request.Source, request.From, request.Targets?.Select(t => new ReferenceTargetRef(t.Target, t.CategoryId)).ToList()), cancellationToken));
    }
}

/// <summary><c>Mode</c>: ThisMonth ou ThisAndNext. <c>Action</c>: Set, Remove ou Restore (só com ThisMonth). <c>Target</c>: TotalExpenses, AllCards, FixedExpenses ou Category.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChangeBudgetRuleRequest(string? Competence, string? Target, Guid? CategoryId, string? Mode, string? Action, decimal? Amount, string? Note);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record BudgetReferenceTargetRequest(string? Target, Guid? CategoryId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ApplyBudgetReferenceRequest(string? Source, string? From, IReadOnlyList<BudgetReferenceTargetRequest>? Targets);
