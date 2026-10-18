using Saldo360.Api.Authentication;
using Saldo360.Application.CreditCards;

namespace Saldo360.Api.CreditCards;

public static class FinancialEmailCollectorEndpoints
{
    public static IEndpointRouteBuilder MapFinancialEmailCollectorEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/financial-email-import/{profile}/run", Run).RequireAuthorization().AddEndpointFilter<ValidateCsrfTokenFilter>();
        app.MapGet("/api/financial-email-import/status", Status).RequireAuthorization();
        return app;
    }
    private static async Task<IResult> Run(string profile, bool dryRun, CurrentUser user, FinancialEmailCollectorRunner runner, CancellationToken cancellationToken)
    {
        if (await user.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        return await runner.RunAsync(profile, userId, dryRun, cancellationToken) is { } result ? Results.Ok(result) : Results.NotFound();
    }
    private static async Task<IResult> Status(CurrentUser user, FinancialEmailCollectorStatusService service, CancellationToken cancellationToken)
    {
        if (await user.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        return Results.Ok(await service.GetAsync(userId, cancellationToken));
    }
}
