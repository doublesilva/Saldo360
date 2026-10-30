using System.Text.Json;
using System.Text.Json.Serialization;
using Saldo360.Api.Authentication;
using Saldo360.Application.Workspaces;

namespace Saldo360.Api.Workspaces;

public static class FinancialResetEndpoints
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record ResetRequest(string? Confirmation);
    public static IEndpointRouteBuilder MapFinancialResetEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/financial-workspaces/{workspaceId:guid}/financial-reset").RequireAuthorization();
        group.MapGet("/preview", async (Guid workspaceId, CurrentUser user, IFinancialWorkspaceResetService service, HttpContext context, CancellationToken ct) =>
            await Run(context, user, async actor => Results.Ok(await service.PreviewAsync(workspaceId, actor, ct)), ct));
        group.MapPost("/", async (Guid workspaceId, CurrentUser user, IFinancialWorkspaceResetService service, HttpContext context, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!context.Request.HasJsonContentType()) return Results.StatusCode(415);
            // Bounded even for chunked requests. Do not log body or confirmation.
            var bytes = new byte[1025]; var length = 0;
            while (length < bytes.Length) { var read = await context.Request.Body.ReadAsync(bytes.AsMemory(length), ct); if (read == 0) break; length += read; }
            if (length > 1024) return Results.StatusCode(413);
            ResetRequest? body;
            try { body = JsonSerializer.Deserialize<ResetRequest>(bytes.AsSpan(0, length), new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
            catch (JsonException) { return Results.BadRequest(new { code = "INVALID_RESET_REQUEST" }); }
            return await Run(context, user, async actor =>
            {
                var result = await service.ResetAsync(workspaceId, actor, body?.Confirmation ?? "", context.TraceIdentifier, ct);
                return Results.Ok(new { result.WorkspaceId, result.Status, deleted = result.Counts });
            }, ct);
        }).AddEndpointFilter<ValidateCsrfTokenFilter>();
        return app;
    }
    private static async Task<IResult> Run(HttpContext context, CurrentUser user, Func<Guid, Task<IResult>> action, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (await user.GetUserIdAsync(ct) is not { } actor) return Results.Unauthorized();
        try { return await action(actor); }
        catch (FinancialResetRejected error)
        {
            return Results.Json(new { code = error.Code }, statusCode: error.Code == "NotFound" ? 404 : error.Code == "InvalidConfirmation" ? 400 : 409);
        }
    }
}
