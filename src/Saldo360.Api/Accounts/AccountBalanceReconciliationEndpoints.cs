using Saldo360.Api.Authentication;
using Saldo360.Application.Accounts.Reconciliation;

namespace Saldo360.Api.Accounts;

/// <summary>
/// T09.5: saldo informado pelo banco × saldo calculado das contas integradas. Somente leitura; valores como texto decimal exato. Nada é ajustado:
/// a diferença é informada e classificada, nunca compensada.
/// <list type="bullet">
/// <item><c>accounts/balance-reconciliation</c>: todas as contas integradas, subtotais PF e PJ separados e o consolidado só quando completo.</item>
/// <item><c>accounts/{id}/balance-reconciliation?from=yyyy-MM&amp;through=yyyy-MM</c>: aberturas mensais (calculada e reconstruída pelo banco) e as observações do período.</item>
/// </list>
/// </summary>
public static class AccountBalanceReconciliationEndpoints
{
    public static IEndpointRouteBuilder MapAccountBalanceReconciliationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/financial-workspaces/{workspaceId:guid}/accounts").RequireAuthorization();
        group.MapGet("/balance-reconciliation", Overview);
        group.MapGet("/{accountId:guid}/balance-reconciliation", Period);
        return app;
    }

    private static async Task<IResult> Overview(Guid workspaceId, CurrentUser user, AccountBalanceReconciliationService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized() : Respond(await service.OverviewAsync(workspaceId, userId, ct));

    private static async Task<IResult> Period(Guid workspaceId, Guid accountId, string? from, string? through, CurrentUser user, AccountBalanceReconciliationService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized() : Respond(await service.PeriodAsync(workspaceId, userId, accountId, from, through, ct));

    private static IResult Respond<T>(ReconciliationResult<T> result) where T : class => result.Access switch
    {
        ReconciliationAccess.NotFound => Results.NotFound(),
        ReconciliationAccess.Invalid => Results.ValidationProblem(result.Errors!),
        _ => Results.Ok(result.Value),
    };
}
