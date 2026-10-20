using System.Text.Json.Serialization;
using Saldo360.Api.Authentication;
using Saldo360.Application.Goals;
using Saldo360.Domain.Common;
using Saldo360.Domain.Goals;
namespace Saldo360.Api.Goals;
public static class FinancialGoalEndpoints
{
    public static IEndpointRouteBuilder MapFinancialGoalEndpoints(this IEndpointRouteBuilder app)
    {
        var g=app.MapGroup("/api/financial-workspaces/{workspaceId:guid}/financial-goals").RequireAuthorization();
        g.MapGet("/",List); g.MapPost("/",Create).AddEndpointFilter<ValidateCsrfTokenFilter>(); g.MapGet("/{id:guid}",Get); g.MapPut("/{id:guid}",Update).AddEndpointFilter<ValidateCsrfTokenFilter>();
        g.MapPost("/{id:guid}/pause",Pause).AddEndpointFilter<ValidateCsrfTokenFilter>(); g.MapPost("/{id:guid}/resume",Resume).AddEndpointFilter<ValidateCsrfTokenFilter>(); g.MapPost("/{id:guid}/complete",Complete).AddEndpointFilter<ValidateCsrfTokenFilter>(); g.MapDelete("/{id:guid}",Archive).AddEndpointFilter<ValidateCsrfTokenFilter>(); return app;
    }
    private static async Task<IResult> List(Guid workspaceId,bool? includeArchived,CurrentUser u,FinancialGoalService s,CancellationToken ct){if(await u.GetUserIdAsync(ct) is not{ } uid)return Results.Unauthorized();var r=await s.ListAsync(workspaceId,uid,includeArchived??false,ct);return r.NotFound?Results.NotFound():Results.Ok(r.Items!.Select(GoalResponse.From));}
    private static async Task<IResult> Get(Guid workspaceId,Guid id,CurrentUser u,FinancialGoalService s,CancellationToken ct)=>await u.GetUserIdAsync(ct) is not{ } uid?Results.Unauthorized():await s.GetAsync(workspaceId,id,uid,ct) is{ } x?Results.Ok(GoalResponse.From(x)):Results.NotFound();
    private static async Task<IResult> Create(Guid workspaceId,GoalRequest? x,CurrentUser u,FinancialGoalService s,CancellationToken ct){if(await u.GetUserIdAsync(ct) is not{ } uid)return Results.Unauthorized();return Result(await s.CreateAsync(workspaceId,uid,x?.ToInput()??new(null,null,null,null,null,null),ct),d=>Results.Created($"/api/financial-workspaces/{workspaceId}/financial-goals/{d.Id}",GoalResponse.From(d)));}
    private static async Task<IResult> Update(Guid workspaceId,Guid id,GoalRequest? x,CurrentUser u,FinancialGoalService s,CancellationToken ct){if(await u.GetUserIdAsync(ct) is not{ } uid)return Results.Unauthorized();return Result(await s.UpdateAsync(workspaceId,id,uid,x?.ToInput()??new(null,null,null,null,null,null),ct),d=>Results.Ok(GoalResponse.From(d)));}
    private static Task<IResult> Pause(Guid f,Guid id,CurrentUser u,FinancialGoalService s,CancellationToken ct)=>Change(f,id,u,s,"pause",ct); private static Task<IResult> Resume(Guid f,Guid id,CurrentUser u,FinancialGoalService s,CancellationToken ct)=>Change(f,id,u,s,"resume",ct); private static Task<IResult> Complete(Guid f,Guid id,CurrentUser u,FinancialGoalService s,CancellationToken ct)=>Change(f,id,u,s,"complete",ct); private static Task<IResult> Archive(Guid f,Guid id,CurrentUser u,FinancialGoalService s,CancellationToken ct)=>Change(f,id,u,s,"archive",ct);
    private static async Task<IResult> Change(Guid f,Guid id,CurrentUser u,FinancialGoalService s,string op,CancellationToken ct){if(await u.GetUserIdAsync(ct) is not{ } uid)return Results.Unauthorized();return Result(await s.ChangeAsync(f,id,uid,op,ct),d=>Results.Ok(GoalResponse.From(d)));}
    private static IResult Result(FinancialGoalResult r,Func<FinancialGoalDto,IResult> ok)=>r.Access switch{FinancialGoalAccess.NotFound=>Results.NotFound(),FinancialGoalAccess.Invalid=>Results.ValidationProblem(r.Errors!),FinancialGoalAccess.Conflict=>Results.Conflict(new{title=r.Message}),_=>ok(r.Goal!)};
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)] public sealed record GoalRequest(string? Name,string? Description,string? GoalType,string? TargetAmount,DateOnly? TargetDate,string? Priority){public FinancialGoalInput ToInput()=>new(Name,Description,GoalType,TargetAmount,TargetDate,Priority);}
public sealed record GoalResponse(Guid Id,Guid WorkspaceId,string Name,string? Description,string GoalType,string TargetAmount,DateOnly? TargetDate,string Priority,string Status,string Currency,DateTimeOffset CreatedAt,DateTimeOffset UpdatedAt,DateTimeOffset? CompletedAt,DateTimeOffset? ArchivedAt){public static GoalResponse From(FinancialGoalDto x)=>new(x.Id,x.WorkspaceId,x.Name,x.Description,x.GoalType.ToString(),Money.Format(x.TargetAmount),x.TargetDate,x.Priority.ToString(),x.Status.ToString(),x.Currency,x.CreatedAt,x.UpdatedAt,x.CompletedAt,x.ArchivedAt);}
