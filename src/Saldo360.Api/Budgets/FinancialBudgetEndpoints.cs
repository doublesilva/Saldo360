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
        var budgets = app.MapGroup("/api/financial-workspaces/{workspaceId:guid}/budgets").RequireAuthorization();
        budgets.MapGet("/", List);
        budgets.MapGet("/{budgetId:guid}", Get);
        budgets.MapGet("/{budgetId:guid}/execution", GetExecution);
        budgets.MapGet("/{budgetId:guid}/targets/{targetId:guid}/composition", GetComposition);
        budgets.MapPost("/", Create).AddEndpointFilter<ValidateCsrfTokenFilter>();
        budgets.MapPut("/{budgetId:guid}", Update).AddEndpointFilter<ValidateCsrfTokenFilter>();
        budgets.MapDelete("/{budgetId:guid}", Archive).AddEndpointFilter<ValidateCsrfTokenFilter>();
        budgets.MapPost("/{budgetId:guid}/targets", AddTarget).AddEndpointFilter<ValidateCsrfTokenFilter>();
        budgets.MapPut("/{budgetId:guid}/targets/{targetId:guid}", UpdateTarget).AddEndpointFilter<ValidateCsrfTokenFilter>();
        budgets.MapDelete("/{budgetId:guid}/targets/{targetId:guid}", ArchiveTarget).AddEndpointFilter<ValidateCsrfTokenFilter>();
        return app;
    }

    private static async Task<IResult> List(
        Guid workspaceId, string? competence, bool? includeArchived, CurrentUser currentUser,
        FinancialBudgetService service, CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        if (competence is not null && !FinancialBudgetService.TryParseCompetence(competence, out _))
            return Results.ValidationProblem(new Dictionary<string, string[]>
                { ["competence"] = ["Use uma competência válida no formato yyyy-MM."] });
        var result = await service.ListAsync(workspaceId, userId, competence, includeArchived ?? false, cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result.Select(FinancialBudgetResponse.From));
    }

    private static async Task<IResult> Get(
        Guid workspaceId, Guid budgetId, CurrentUser currentUser, FinancialBudgetService service,
        CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        var result = await service.GetAsync(workspaceId, budgetId, userId, cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(FinancialBudgetResponse.From(result));
    }

    private static async Task<IResult> GetExecution(
        Guid workspaceId, Guid budgetId, CurrentUser currentUser, FinancialBudgetExecutionService service,
        CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        var result = await service.GetAsync(workspaceId, budgetId, userId, cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(FinancialBudgetExecutionResponse.From(result));
    }

    /// <summary>Registros que compõem o consumo de uma meta (mesma lista e regra do resumo), paginados. O total vem do servidor.</summary>
    private static async Task<IResult> GetComposition(
        Guid workspaceId, Guid budgetId, Guid targetId, int? page, int? pageSize, CurrentUser currentUser, FinancialBudgetExecutionService service,
        CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        var result = await service.GetCompositionAsync(workspaceId, budgetId, targetId, userId, page, pageSize, cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(BudgetCompositionResponse.From(result));
    }

    private static async Task<IResult> Create(
        Guid workspaceId, CreateFinancialBudgetRequest? request, CurrentUser currentUser, FinancialBudgetService service,
        CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        var result = await service.CreateAsync(workspaceId, userId, request?.Competence, request?.Description, cancellationToken);
        return ToResponse(result, budget => Results.Created(
            $"/api/financial-workspaces/{workspaceId}/budgets/{budget.Id}", FinancialBudgetResponse.From(budget)));
    }

    private static async Task<IResult> Update(
        Guid workspaceId, Guid budgetId, UpdateFinancialBudgetRequest? request, CurrentUser currentUser,
        FinancialBudgetService service, CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        return ToResponse(await service.UpdateAsync(
            workspaceId, budgetId, userId, request?.Description, cancellationToken),
            budget => Results.Ok(FinancialBudgetResponse.From(budget)));
    }

    private static async Task<IResult> Archive(
        Guid workspaceId, Guid budgetId, CurrentUser currentUser, FinancialBudgetService service,
        CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        return ToResponse(await service.ArchiveAsync(workspaceId, budgetId, userId, cancellationToken), _ => Results.NoContent());
    }

    private static async Task<IResult> AddTarget(
        Guid workspaceId, Guid budgetId, CreateFinancialBudgetTargetRequest? request, CurrentUser currentUser,
        FinancialBudgetService service, CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        var result = await service.AddTargetAsync(workspaceId, budgetId, userId, request?.Name, request?.PlannedAmount,
            request?.GroupingType, request?.CategoryId, cancellationToken);
        return ToResponse(result, budget => Results.Created(
            $"/api/financial-workspaces/{workspaceId}/budgets/{budgetId}", FinancialBudgetResponse.From(budget)));
    }

    private static async Task<IResult> UpdateTarget(
        Guid workspaceId, Guid budgetId, Guid targetId, UpdateFinancialBudgetTargetRequest? request,
        CurrentUser currentUser, FinancialBudgetService service, CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        return ToResponse(await service.UpdateTargetAsync(
            workspaceId, budgetId, targetId, userId, request?.Name, request?.PlannedAmount, cancellationToken),
            budget => Results.Ok(FinancialBudgetResponse.From(budget)));
    }

    private static async Task<IResult> ArchiveTarget(
        Guid workspaceId, Guid budgetId, Guid targetId, CurrentUser currentUser, FinancialBudgetService service,
        CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        return ToResponse(await service.ArchiveTargetAsync(
            workspaceId, budgetId, targetId, userId, cancellationToken), _ => Results.NoContent());
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
    FinancialBudgetCategoryDto? Category,
    bool IsArchived,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt)
{
    public static FinancialBudgetTargetResponse From(FinancialBudgetTargetDto dto) => new(
        dto.Id, dto.Name, Money.Format(dto.PlannedAmount), dto.GroupingType, dto.Category, dto.IsArchived,
        dto.CreatedAt, dto.UpdatedAt, dto.ArchivedAt);
}

/// <summary>
/// Execução do mês. <c>categories</c> resume só as metas de categoria (que não se sobrepõem); fixas e cartões vêm em <c>targets</c> e nunca entram em
/// uma soma global, porque contam os mesmos gastos por outra perspectiva.
/// </summary>
public sealed record FinancialBudgetExecutionResponse(
    Guid BudgetId,
    string Competence,
    FinancialBudgetExecutionSummaryResponse Categories,
    IReadOnlyList<FinancialBudgetTargetExecutionResponse> Targets)
{
    public static FinancialBudgetExecutionResponse From(FinancialBudgetExecutionDto dto) => new(
        dto.BudgetId,
        dto.Competence.ToString("yyyy-MM", CultureInfo.InvariantCulture),
        FinancialBudgetExecutionSummaryResponse.From(dto.Categories),
        dto.Targets.Select(FinancialBudgetTargetExecutionResponse.From).ToList());
}

public sealed record BudgetCoverageResponse(int EstimatedCompetenceCount, int UndefinedNatureCount, string UndefinedNatureAmount, bool NoCards)
{
    public static BudgetCoverageResponse From(BudgetCoverage? coverage) => coverage is null
        ? new(0, 0, Money.Format(0m), false)
        : new(coverage.EstimatedCompetenceCount, coverage.UndefinedNatureCount, Money.Format(coverage.UndefinedNatureAmount), coverage.NoCards);
}

public sealed record FinancialBudgetTargetExecutionResponse(
    Guid TargetId,
    BudgetTargetGroupingType GroupingType,
    Guid? CategoryId,
    string Planned,
    string Consumed,
    string Realized,
    string CreditCardCommitted,
    string Committed,
    string ProjectedConsumption,
    string Available,
    string AvailableProjected,
    string ExceededAmount,
    string ExceededProjected,
    string? ConsumptionPercentage,
    string? ProjectedPercentage,
    FinancialBudgetAlertStatus Status,
    FinancialBudgetAlertStatus ProjectedStatus,
    string AlertMessage,
    BudgetCoverageResponse Coverage)
{
    public static FinancialBudgetTargetExecutionResponse From(FinancialBudgetTargetExecutionDto dto)
    {
        var amounts = FinancialBudgetExecutionAmountsResponse.From(dto.Amounts);
        return new(dto.TargetId, dto.GroupingType, dto.CategoryId, amounts.Planned, amounts.Consumed, amounts.Realized, amounts.CreditCardCommitted, amounts.Committed,
            amounts.ProjectedConsumption, amounts.Available, amounts.AvailableProjected, amounts.ExceededAmount, amounts.ExceededProjected,
            amounts.ConsumptionPercentage, amounts.ProjectedPercentage, dto.Status, dto.ProjectedStatus, dto.AlertMessage, BudgetCoverageResponse.From(dto.Coverage));
    }
}

public sealed record FinancialBudgetExecutionAmountsResponse(
    string Planned,
    string Consumed,
    string Realized,
    string CreditCardCommitted,
    string Committed,
    string ProjectedConsumption,
    string Available,
    string AvailableProjected,
    string ExceededAmount,
    string ExceededProjected,
    string? ConsumptionPercentage,
    string? ProjectedPercentage)
{
    private static string? Percent(decimal? value) => value is { } percentage ? percentage.ToString("F2", CultureInfo.InvariantCulture) : null;

    public static FinancialBudgetExecutionAmountsResponse From(FinancialBudgetExecutionAmounts amounts) => new(
        Money.Format(amounts.Planned),
        Money.Format(amounts.Consumed),
        Money.Format(amounts.Realized),
        Money.Format(amounts.CreditCardCommitted),
        Money.Format(amounts.Committed),
        Money.Format(amounts.ProjectedConsumption),
        Money.Format(amounts.Available),
        Money.Format(amounts.AvailableProjected),
        Money.Format(amounts.ExceededAmount),
        Money.Format(amounts.ExceededProjected),
        Percent(amounts.ConsumptionPercentage),
        Percent(amounts.ProjectedPercentage));
}

public sealed record FinancialBudgetExecutionSummaryResponse(
    string Planned,
    string Consumed,
    string Realized,
    string CreditCardCommitted,
    string Committed,
    string ProjectedConsumption,
    string Available,
    string AvailableProjected,
    string ExceededAmount,
    string? ConsumptionPercentage,
    FinancialBudgetAlertStatus Status,
    string AlertMessage,
    int ApproachingCount,
    int ReachedCount,
    int ExceededCount,
    int TargetCount,
    string ExceededProjected,
    string? ProjectedPercentage,
    FinancialBudgetAlertStatus ProjectedStatus)
{
    public static FinancialBudgetExecutionSummaryResponse From(FinancialBudgetExecutionSummary summary) => new(
        Money.Format(summary.Planned),
        Money.Format(summary.Consumed),
        Money.Format(summary.Realized),
        Money.Format(summary.CreditCardCommitted),
        Money.Format(summary.Committed),
        Money.Format(summary.ProjectedConsumption),
        Money.Format(summary.Available),
        Money.Format(summary.AvailableProjected),
        Money.Format(summary.ExceededAmount),
        summary.ConsumptionPercentage is { } percentage ? percentage.ToString("F2", CultureInfo.InvariantCulture) : null,
        summary.Status,
        summary.AlertMessage,
        summary.ApproachingCount,
        summary.ReachedCount,
        summary.ExceededCount,
        summary.TargetCount,
        Money.Format(summary.ExceededProjected),
        summary.ProjectedPercentage is { } projected ? projected.ToString("F2", CultureInfo.InvariantCulture) : null,
        summary.ProjectedStatus);
}

public sealed record BudgetCompositionItemResponse(
    BudgetFactOrigin Origin,
    Guid RecordId,
    DateOnly Date,
    string Amount,
    BudgetFactState State,
    Guid? CategoryId,
    string? CategoryPath,
    Saldo360.Domain.Categories.ExpenseNature ExpenseNature,
    string Description,
    Guid? CardId,
    string? CardName,
    string? AccountName,
    string? InvoiceCompetence,
    bool CompetenceReliable);

public sealed record BudgetCompositionCardResponse(Guid CardId, string CardName, string Consumed, int Count);

public sealed record BudgetCompositionResponse(
    Guid BudgetId,
    string Competence,
    Guid TargetId,
    BudgetTargetGroupingType GroupingType,
    FinancialBudgetExecutionAmountsResponse Amounts,
    FinancialBudgetAlertStatus Status,
    BudgetCoverageResponse Coverage,
    int Page,
    int PageSize,
    int TotalCount,
    IReadOnlyList<BudgetCompositionItemResponse> Items,
    IReadOnlyList<BudgetCompositionCardResponse> Cards)
{
    public static BudgetCompositionResponse From(BudgetCompositionDto dto) => new(
        dto.BudgetId, dto.Competence.ToString("yyyy-MM", CultureInfo.InvariantCulture), dto.TargetId, dto.GroupingType,
        FinancialBudgetExecutionAmountsResponse.From(dto.Amounts), dto.Status, BudgetCoverageResponse.From(dto.Coverage), dto.Page, dto.PageSize, dto.TotalCount,
        dto.Items.Select(item => new BudgetCompositionItemResponse(item.Origin, item.RecordId, item.Date, Money.Format(item.Amount), item.State, item.CategoryId, item.CategoryPath,
            item.Nature, item.Description, item.CardId, item.CardName, item.AccountName, item.InvoiceCompetence, item.CompetenceReliable)).ToList(),
        dto.Cards.Select(card => new BudgetCompositionCardResponse(card.CardId, card.CardName, Money.Format(card.Consumed), card.Count)).ToList());
}
