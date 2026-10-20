using Saldo360.Api.Authentication;
using Saldo360.Application.Workspaces;

namespace Saldo360.Api.Workspaces;

public static class FinancialWorkspaceEndpoints
{
    public static IEndpointRouteBuilder MapFinancialWorkspaceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/me/financial-workspace", async (CurrentUser user, IFinancialWorkspaceRepository repository, TimeProvider time, HttpContext context, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (await user.GetUserIdAsync(ct) is not { } id) return Results.Unauthorized();
            if (context.Request.ContentLength is > 0)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = ["O provisionamento Personal não recebe parâmetros. Shared não está habilitado."] });
            var workspace = await repository.EnsurePersonalAsync(id, time.GetUtcNow(), ct);
            if (!workspace.IsActive) return Results.Conflict(new { code = "WORKSPACE_INACTIVE" });
            return Results.Ok(new { workspace.Id, workspace.OwnerUserId, workspace.Name, workspace.Type, workspace.IsActive, workspace.CreatedAt, workspace.UpdatedAt });
        }).RequireAuthorization().AddEndpointFilter<ValidateCsrfTokenFilter>();
        return app;
    }
}
