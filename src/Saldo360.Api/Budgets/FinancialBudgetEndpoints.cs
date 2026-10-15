using System.Globalization;
using System.Text.Json.Serialization;
using Saldo360.Api.Authentication;
using Saldo360.Application.Budgets;
using Saldo360.Domain.Budgets;
using Saldo360.Domain.Common;

namespace Saldo360.Api.Budgets;

public static class FinancialBudgetEndpoints
{
    public static IEndpointRouteBuilder MapFinancialBudgetEndpoints(this IEndpointRouteBuilder app)
    {
        var budgets = app.MapGroup("/api/families/{familyId:guid}/budgets").RequireAuthorization();
        budgets.MapGet("/", List);
        budgets.MapGet("/{budgetId:guid}", Get);
        budgets.MapGet("/{budgetId:guid}/execution", GetExecution);
        budgets.MapPost("/", Create).AddEndpointFilter<ValidateCsrfTokenFilter>();
        budgets.MapPut("/{budgetId:guid}", Update).AddEndpointFilter<ValidateCsrfTokenFilter>();
        budgets.MapDelete("/{budgetId:guid}", Archive).AddEndpointFilter<ValidateCsrfTokenFilter>();
        budgets.MapPost("/{budgetId:guid}/targets", AddTarget).AddEndpointFilter<ValidateCsrfTokenFilter>();
        budgets.MapPut("/{budgetId:guid}/targets/{targetId:guid}", UpdateTarget).AddEndpointFilter<ValidateCsrfTokenFilter>();
        budgets.MapDelete("/{budgetId:guid}/targets/{targetId:guid}", ArchiveTarget).AddEndpointFilter<ValidateCsrfTokenFilter>();
        return app;
    }

    private static async Task<IResult> List(
        Guid familyId, string? competence, bool? includeArchived, CurrentUser currentUser,
        FinancialBudgetService service, CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        if (competence is not null && !FinancialBudgetService.TryParseCompetence(competence, out _))
            return Results.ValidationProblem(new Dictionary<string, string[]>
                { ["competence"] = ["Use uma competência válida no formato yyyy-MM."] });
        var result = await service.ListAsync(familyId, userId, competence, includeArchived ?? false, cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result.Select(FinancialBudgetResponse.From));
    }

    private static async Task<IResult> Get(
        Guid familyId, Guid budgetId, CurrentUser currentUser, FinancialBudgetService service,
        CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        var result = await service.GetAsync(familyId, budgetId, userId, cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(FinancialBudgetResponse.From(result));
    }

    private static async Task<IResult> GetExecution(
        Guid familyId, Guid budgetId, CurrentUser currentUser, FinancialBudgetExecutionService service,
        CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        var result = await service.GetAsync(familyId, budgetId, userId, cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(FinancialBudgetExecutionResponse.From(result));
    }

    private static async Task<IResult> Create(
        Guid familyId, CreateFinancialBudgetRequest? request, CurrentUser currentUser, FinancialBudgetService service,
        CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        var result = await service.CreateAsync(familyId, userId, request?.Competence, request?.Description, cancellationToken);
        return ToResponse(result, budget => Results.Created(
            $"/api/families/{familyId}/budgets/{budget.Id}", FinancialBudgetResponse.From(budget)));
    }

    private static async Task<IResult> Update(
        Guid familyId, Guid budgetId, UpdateFinancialBudgetRequest? request, CurrentUser currentUser,
        FinancialBudgetService service, CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        return ToResponse(await service.UpdateAsync(
            familyId, budgetId, userId, request?.Description, cancellationToken),
            budget => Results.Ok(FinancialBudgetResponse.From(budget)));
    }

    private static async Task<IResult> Archive(
        Guid familyId, Guid budgetId, CurrentUser currentUser, FinancialBudgetService service,
        CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        return ToResponse(await service.ArchiveAsync(familyId, budgetId, userId, cancellationToken), _ => Results.NoContent());
    }

    private static async Task<IResult> AddTarget(
        Guid familyId, Guid budgetId, CreateFinancialBudgetTargetRequest? request, CurrentUser currentUser,
        FinancialBudgetService service, CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        var result = await service.AddTargetAsync(familyId, budgetId, userId, request?.Name, request?.PlannedAmount,
            request?.GroupingType, request?.CategoryId, cancellationToken);
        return ToResponse(result, budget => Results.Created(
            $"/api/families/{familyId}/budgets/{budgetId}", FinancialBudgetResponse.From(budget)));
    }

    private static async Task<IResult> UpdateTarget(
        Guid familyId, Guid budgetId, Guid targetId, UpdateFinancialBudgetTargetRequest? request,
        CurrentUser currentUser, FinancialBudgetService service, CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        return ToResponse(await service.UpdateTargetAsync(
            familyId, budgetId, targetId, userId, request?.Name, request?.PlannedAmount, cancellationToken),
            budget => Results.Ok(FinancialBudgetResponse.From(budget)));
    }

    private static async Task<IResult> ArchiveTarget(
        Guid familyId, Guid budgetId, Guid targetId, CurrentUser currentUser, FinancialBudgetService service,
        CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        return ToResponse(await service.ArchiveTargetAsync(
            familyId, budgetId, targetId, userId, cancellationToken), _ => Results.NoContent());
    }

    private static IResult ToResponse(BudgetResult result, Func<FinancialBudgetDto, IResult> ok) => result.Access switch
    {
        BudgetAccess.NotFound => Results.NotFound(),
        BudgetAccess.Invalid => Results.ValidationProblem(result.Errors!),
        BudgetAccess.Conflict => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: result.Message),
        _ => ok(result.Budget!),
    };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateFinancialBudgetRequest(string? Competence, string? Description);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateFinancialBudgetRequest(string? Description);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateFinancialBudgetTargetRequest(
    string? Name, decimal? PlannedAmount, string? GroupingType, Guid? CategoryId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateFinancialBudgetTargetRequest(string? Name, decimal? PlannedAmount);

public sealed record FinancialBudgetResponse(
    Guid Id,
    string Competence,
    string? Description,
    bool IsArchived,
    string TotalPlannedAmount,
    IReadOnlyList<FinancialBudgetTargetResponse> Targets,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt)
{
    public static FinancialBudgetResponse From(FinancialBudgetDto dto) => new(
        dto.Id, dto.Competence.ToString("yyyy-MM", CultureInfo.InvariantCulture), dto.Description, dto.IsArchived,
        Money.Format(dto.TotalPlannedAmount), dto.Targets.Select(FinancialBudgetTargetResponse.From).ToList(),
        dto.CreatedAt, dto.UpdatedAt, dto.ArchivedAt);
}

public sealed record FinancialBudgetTargetResponse(
    Guid Id,
    string Name,
    string PlannedAmount,
    BudgetTargetGroupingType GroupingType,
    FinancialBudgetCategoryDto Category,
    bool IsArchived,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt)
{
    public static FinancialBudgetTargetResponse From(FinancialBudgetTargetDto dto) => new(
        dto.Id, dto.Name, Money.Format(dto.PlannedAmount), dto.GroupingType, dto.Category, dto.IsArchived,
        dto.CreatedAt, dto.UpdatedAt, dto.ArchivedAt);
}

public sealed record FinancialBudgetExecutionResponse(
    Guid BudgetId,
    string Competence,
    FinancialBudgetExecutionAmountsResponse Total,
    IReadOnlyList<FinancialBudgetTargetExecutionResponse> Targets)
{
    public static FinancialBudgetExecutionResponse From(FinancialBudgetExecutionDto dto) => new(
        dto.BudgetId,
        dto.Competence.ToString("yyyy-MM", CultureInfo.InvariantCulture),
        FinancialBudgetExecutionAmountsResponse.From(dto.Total),
        dto.Targets.Select(FinancialBudgetTargetExecutionResponse.From).ToList());
}

public sealed record FinancialBudgetTargetExecutionResponse(
    Guid TargetId,
    Guid CategoryId,
    string Planned,
    string Realized,
    string Committed,
    string ProjectedConsumption,
    string Available,
    string ExceededAmount,
    string? ConsumptionPercentage)
{
    public static FinancialBudgetTargetExecutionResponse From(FinancialBudgetTargetExecutionDto dto)
    {
        var amounts = FinancialBudgetExecutionAmountsResponse.From(dto.Amounts);
        return new(dto.TargetId, dto.CategoryId, amounts.Planned, amounts.Realized, amounts.Committed,
            amounts.ProjectedConsumption, amounts.Available, amounts.ExceededAmount, amounts.ConsumptionPercentage);
    }
}

public sealed record FinancialBudgetExecutionAmountsResponse(
    string Planned,
    string Realized,
    string Committed,
    string ProjectedConsumption,
    string Available,
    string ExceededAmount,
    string? ConsumptionPercentage)
{
    public static FinancialBudgetExecutionAmountsResponse From(FinancialBudgetExecutionAmounts amounts) => new(
        Money.Format(amounts.Planned),
        Money.Format(amounts.Realized),
        Money.Format(amounts.Committed),
        Money.Format(amounts.ProjectedConsumption),
        Money.Format(amounts.Available),
        Money.Format(amounts.ExceededAmount),
        amounts.ConsumptionPercentage is { } percentage
            ? percentage.ToString("F2", CultureInfo.InvariantCulture)
            : null);
}
