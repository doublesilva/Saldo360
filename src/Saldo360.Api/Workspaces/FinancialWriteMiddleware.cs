using Microsoft.EntityFrameworkCore;
using Npgsql;
using Saldo360.Application.CreditCards;
using Saldo360.Application.Workspaces;

namespace Saldo360.Api.Workspaces;

public sealed class FinancialWriteMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.Value?.Contains("/financial-reset", StringComparison.Ordinal) == true)
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.OnStarting(() => { context.Response.Headers.CacheControl = "no-store"; return Task.CompletedTask; });
        }
        Guid? target = null;
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            if (Guid.TryParse(context.Request.RouteValues["workspaceId"]?.ToString(), out var w)) target = w;
            else if (context.Request.Path.StartsWithSegments("/api/financial-email-import"))
                target = context.RequestServices.GetService<FinancialEmailImportOptions>()?.Sources.SingleOrDefault(x => string.Equals(x.Name, context.Request.RouteValues["profile"]?.ToString(), StringComparison.OrdinalIgnoreCase))?.WorkspaceId;
        }
        using var scope = FinancialWriteScope.Enter(target);
        try { await next(context); }
        catch (Exception error) when (!context.Response.HasStarted && Conflict(error))
        {
            context.Response.Clear(); context.Response.StatusCode = 409; context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsJsonAsync(new { code = "FINANCIAL_WORKSPACE_CHANGED", message = "O orçamento mudou durante a operação. Recarregue os dados antes de tentar novamente.", traceId = context.TraceIdentifier });
        }
    }
    private static bool Conflict(Exception error) => error is DbUpdateConcurrencyException || error is PostgresException pg &&
        (pg.SqlState is "40001" or "40P01" || pg.SqlState == "P0001" && pg.MessageText == "FINANCIAL_WORKSPACE_RESET_CONFLICT")
        || error.InnerException is { } inner && Conflict(inner);
}
