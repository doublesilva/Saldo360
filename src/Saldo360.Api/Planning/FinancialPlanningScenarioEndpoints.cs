using Saldo360.Api.Authentication;
using Saldo360.Application.Planning;
using Saldo360.Domain.Planning;
using Saldo360.Domain.Common;

namespace Saldo360.Api.Planning;

public static class FinancialPlanningScenarioEndpoints
{
    public static IEndpointRouteBuilder MapFinancialPlanningScenarioEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/families/{familyId:guid}/planning-scenarios").RequireAuthorization();
        group.MapGet("/", List);
        group.MapPost("/", Create).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapGet("/{id:guid}", Get);
        group.MapPut("/{id:guid}", Update).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapDelete("/{id:guid}", Archive).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapGet("/{id:guid}/parameters", ListParameters);
        group.MapPut("/{id:guid}/parameters/{competence}", UpsertParameter).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapGet("/{id:guid}/projection", Projection);
        return app;
    }

    private static async Task<IResult> List(Guid familyId, bool? includeArchived, CurrentUser user, FinancialPlanningScenarioService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } uid ? Results.Unauthorized() : (await service.ListAsync(familyId, uid, includeArchived ?? false, ct)) is { } items ? Results.Ok(items.Select(ScenarioResponse.From)) : Results.NotFound();

    private static async Task<IResult> Create(Guid familyId, ScenarioRequest? request, CurrentUser user, FinancialPlanningScenarioService service, CancellationToken ct)
    {
        if (await user.GetUserIdAsync(ct) is not { } uid) return Results.Unauthorized();
        var result = await service.CreateAsync(familyId, uid, new(request?.Name, request?.Description, request?.StartMonth, request?.Months, request?.RecoveryAccountId), ct);
        return Result(result, x => Results.Created($"/api/families/{familyId}/planning-scenarios/{x.Id}", ScenarioResponse.From(x)));
    }
    private static async Task<IResult> Get(Guid familyId, Guid id, CurrentUser user, FinancialPlanningScenarioService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } uid ? Results.Unauthorized() : await service.GetAsync(familyId, id, uid, ct) is { } item ? Results.Ok(ScenarioResponse.From(item)) : Results.NotFound();
    private static async Task<IResult> Update(Guid familyId, Guid id, ScenarioRequest? request, CurrentUser user, FinancialPlanningScenarioService service, CancellationToken ct)
    {
        if (await user.GetUserIdAsync(ct) is not { } uid) return Results.Unauthorized();
        return Result(await service.UpdateAsync(familyId, id, uid, new(request?.Name, request?.Description, request?.StartMonth, request?.Months, request?.RecoveryAccountId), ct), x => Results.Ok(ScenarioResponse.From(x)));
    }
    private static async Task<IResult> Archive(Guid familyId, Guid id, CurrentUser user, FinancialPlanningScenarioService service, CancellationToken ct)
    {
        if (await user.GetUserIdAsync(ct) is not { } uid) return Results.Unauthorized();
        return Result(await service.ArchiveAsync(familyId, id, uid, ct), x => Results.Ok(ScenarioResponse.From(x)));
    }
    private static async Task<IResult> ListParameters(Guid familyId, Guid id, CurrentUser user, FinancialPlanningScenarioService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } uid ? Results.Unauthorized() : await service.ListParametersAsync(familyId, id, uid, ct) is { } items ? Results.Ok(items.Select(ParameterResponse.From)) : Results.NotFound();
    private static async Task<IResult> UpsertParameter(Guid familyId, Guid id, string competence, ParameterRequest? request, CurrentUser user, FinancialPlanningScenarioService service, CancellationToken ct)
    {
        if (await user.GetUserIdAsync(ct) is not { } uid) return Results.Unauthorized();
        var result = await service.UpsertParameterAsync(familyId, id, competence, uid, new(request?.ExpectedAdditionalIncome, request?.ExpectedAdditionalExpense, request?.ContingencyReserve, request?.RecoveryTargetPercentage, request?.Notes), ct);
        return result.Access switch { PlanningScenarioAccess.NotFound => Results.NotFound(), PlanningScenarioAccess.Invalid => Results.ValidationProblem(result.Errors!), PlanningScenarioAccess.Conflict => Results.Conflict(new { title = result.Message }), _ => Results.Ok(ParameterResponse.From(result.Parameter!)) };
    }
    private static async Task<IResult> Projection(Guid familyId, Guid id, CurrentUser user, FinancialPlanningScenarioProjectionService service, CancellationToken ct)
    {
        if (await user.GetUserIdAsync(ct) is not { } uid) return Results.Unauthorized();
        var result = await service.GetAsync(familyId, id, uid, ct);
        if (result.NotFound) return Results.NotFound();
        if (result.Errors is not null) return Results.ValidationProblem(result.Errors);
        return Results.Ok(ScenarioProjectionResponse.Create(result.Projection!));
    }
    private static IResult Result(PlanningScenarioResult result, Func<PlanningScenarioDto, IResult> ok) => result.Access switch { PlanningScenarioAccess.NotFound => Results.NotFound(), PlanningScenarioAccess.Invalid => Results.ValidationProblem(result.Errors!), PlanningScenarioAccess.Conflict => Results.Conflict(new { title = result.Message }), _ => ok(result.Scenario!) };
}

public sealed record ScenarioRequest(string? Name, string? Description, string? StartMonth, int? Months, Guid? RecoveryAccountId = null);
public sealed record ParameterRequest(string? ExpectedAdditionalIncome, string? ExpectedAdditionalExpense, string? ContingencyReserve, string? RecoveryTargetPercentage, string? Notes);
public sealed record ScenarioResponse(Guid Id, Guid FamilyId, Guid? RecoveryAccountId, string Name, string? Description, string StartMonth, int Months, PlanningScenarioStatus Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ArchivedAt)
{
    public static ScenarioResponse From(PlanningScenarioDto x) => new(x.Id, x.FamilyId, x.RecoveryAccountId, x.Name, x.Description, x.StartMonth.ToString("yyyy-MM"), x.Months, x.Status, x.CreatedAt, x.UpdatedAt, x.ArchivedAt);
}
public sealed record ParameterResponse(Guid Id, Guid FamilyId, Guid ScenarioId, string Competence, string ExpectedAdditionalIncome, string ExpectedAdditionalExpense, string ContingencyReserve, string RecoveryTargetPercentage, string? Notes)
{
    public static ParameterResponse From(PlanningMonthlyParameterDto x) => new(x.Id, x.FamilyId, x.ScenarioId, x.Competence.ToString("yyyy-MM"), Money.Format(x.ExpectedAdditionalIncome), Money.Format(x.ExpectedAdditionalExpense), Money.Format(x.ContingencyReserve), Money.Format(x.RecoveryTargetPercentage), x.Notes);
}

public sealed record ScenarioProjectionResponse(Guid ScenarioId, string From, int Months, IReadOnlyList<ScenarioProjectionMonthResponse> Items)
{
    public static ScenarioProjectionResponse Create(PlanningScenarioProjection x) => new(x.ScenarioId, x.From.ToString("yyyy-MM"), x.Months, x.Items.Select(ScenarioProjectionMonthResponse.From).ToList());
}
public sealed record ScenarioProjectionMonthResponse(string Competence, string? OpeningBalance, string RealizedIncome, string RealizedExpense, string PendingIncome, string PendingExpense, string CreditCardOutstanding, string? BaseClosingBalance, string AdditionalIncome, string AdditionalExpense, string CumulativeAdjustment, string? ScenarioClosingBalance, string ContingencyReserve, string? PlannedAvailable, bool ProjectionComplete, bool UsesBaseProjectionOnly, bool HasUnallocatedObligations, string RecoveryTargetPercentage, Guid? RecoveryAccountId, string? OpeningNegativeBalance, string? ClosingNegativeBalance, string? ProjectedRecovery, string? CumulativeRecovery, string? RecoveryCapacity, string? RecoveryTarget, string? UncommittedMargin, string? RemainingNegativeBalance, RecoveryStatus RecoveryStatus, bool RecoveryProjectionComplete)
{
    public static ScenarioProjectionMonthResponse From(PlanningScenarioProjectionMonth x) => new(x.Competence.ToString("yyyy-MM"), Format(x.OpeningBalance), Money.Format(x.RealizedIncome), Money.Format(x.RealizedExpense), Money.Format(x.PendingIncome), Money.Format(x.PendingExpense), Money.Format(x.CreditCardOutstanding), Format(x.BaseClosingBalance), Money.Format(x.AdditionalIncome), Money.Format(x.AdditionalExpense), Money.Format(x.CumulativeAdjustment), Format(x.ScenarioClosingBalance), Money.Format(x.ContingencyReserve), Format(x.PlannedAvailable), x.ProjectionComplete, x.UsesBaseProjectionOnly, x.HasUnallocatedObligations, Money.Format(x.RecoveryTargetPercentage), x.RecoveryAccountId, Format(x.OpeningNegativeBalance), Format(x.ClosingNegativeBalance), Format(x.ProjectedRecovery), Format(x.CumulativeRecovery), Format(x.RecoveryCapacity), Format(x.RecoveryTarget), Format(x.UncommittedMargin), Format(x.RemainingNegativeBalance), x.RecoveryStatus, x.RecoveryProjectionComplete);
    private static string? Format(decimal? value) => value is { } number ? Money.Format(number) : null;
}
