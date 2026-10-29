using Saldo360.Api.Authentication;
using Saldo360.Application.CreditCards.Projection;

namespace Saldo360.Api.CreditCards;

/// <summary>
/// T09.4: leituras das obrigações dos cartões. Todo valor é calculado no backend em decimal e devolvido como texto decimal exato; nada é gravado.
/// <list type="bullet">
/// <item><c>invoice-overview?competence=yyyy-MM</c>: os cartões do orçamento e suas faturas do mês, com fechamento, pagamento e conciliação separados.</item>
/// <item><c>obligation-projection?from=yyyy-MM&amp;months=6</c>: a projeção mensal por cartão e por conta pagadora.</item>
/// <item><c>credit-cards/{cardId}/projected-installments</c>: as compras parceladas (conhecidas e estimadas) do cartão.</item>
/// </list>
/// </summary>
public static class CardProjectionEndpoints
{
    public static IEndpointRouteBuilder MapCardProjectionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/financial-workspaces/{workspaceId:guid}/credit-cards").RequireAuthorization();
        group.MapGet("/invoice-overview", Overview);
        group.MapGet("/obligation-projection", Projection);
        group.MapGet("/{cardId:guid}/projected-installments", Installments);
        return app;
    }

    private static async Task<IResult> Overview(Guid workspaceId, string? competence, CurrentUser user, CardProjectionService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized() : Respond(await service.InvoiceOverviewAsync(workspaceId, userId, competence, ct));

    private static async Task<IResult> Projection(Guid workspaceId, string? from, int? months, CurrentUser user, CardProjectionService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized() : Respond(await service.ObligationProjectionAsync(workspaceId, userId, from, months, ct));

    private static async Task<IResult> Installments(Guid workspaceId, Guid cardId, CurrentUser user, CardProjectionService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized() : Respond(await service.InstallmentsAsync(workspaceId, userId, cardId, ct));

    private static IResult Respond<T>(CardProjectionResult<T> result) where T : class => result.Access switch
    {
        CardProjectionAccess.NotFound => Results.NotFound(),
        CardProjectionAccess.Invalid => Results.ValidationProblem(result.Errors!),
        _ => Results.Ok(result.Value),
    };
}
