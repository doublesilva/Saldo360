using Saldo360.Api.Authentication;
using Saldo360.Application.CreditCards;

namespace Saldo360.Api.CreditCards;

public static class FinancialEmailCollectorEndpoints
{
    public static IEndpointRouteBuilder MapFinancialEmailCollectorEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/financial-email-import/{profile}/run", Run).RequireAuthorization().AddEndpointFilter<ValidateCsrfTokenFilter>();
        app.MapPost("/api/financial-email-import/{profile}/historical-scan", Historical).RequireAuthorization().AddEndpointFilter<ValidateCsrfTokenFilter>();
        app.MapGet("/api/financial-email-import/status", Status).RequireAuthorization();
        return app;
    }
    private static async Task<IResult> Historical(string profile, FinancialEmailHistoricalRequest request, CurrentUser user, FinancialEmailCollectorRunner runner, CancellationToken cancellationToken)
    {
        if (await user.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        if (request.DryRun is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["dryRun"] = ["Informe dryRun explicitamente; revise a prévia antes de coletar."] });
        FinancialEmailHistoricalWindow window;
        try { window = FinancialEmailHistoricalWindow.Create(request.From, request.Through, request.MaxMessages ?? 50); }
        catch (ArgumentException) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["window"] = ["Informe datas yyyy-MM-dd válidas, período inclusivo de até 366 dias e limite de 1 a 500 mensagens."] }); }
        return await runner.RunHistoricalAsync(profile, userId, request.DryRun.Value, window, cancellationToken) is { } result ? Results.Ok(result) : Results.NotFound();
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
public sealed record FinancialEmailHistoricalRequest(bool? DryRun, string? From, string? Through, int? MaxMessages);
