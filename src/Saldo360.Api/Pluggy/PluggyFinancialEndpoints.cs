using System.Text.Json.Serialization;
using Saldo360.Api.Authentication;
using Saldo360.Application.Pluggy;

namespace Saldo360.Api.Pluggy;

/// <summary>
/// Integração das contas Pluggy ao cadastro financeiro (T09.3). Mesma autenticação, autorização (dono do orçamento) e CSRF das demais rotas
/// financeiras. Nenhuma rota inicia operação financeira externa, e identificadores externos recebidos do navegador são sempre conferidos
/// no backend contra o Item do orçamento. Respostas não são armazenadas em cache.
/// </summary>
public static class PluggyFinancialEndpoints
{
    public static IEndpointRouteBuilder MapPluggyFinancialEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/financial-workspaces/{workspaceId:guid}/pluggy-integration").RequireAuthorization();
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        group.MapGet("/status", Status);
        group.MapGet("/connections", ListConnections);
        group.MapGet("/connections/{connectionId:guid}/accounts", ListAvailable);
        group.MapGet("/links", ListLinks);
        group.MapPost("/links", Link).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapGet("/links/{linkId:guid}", GetLink);
        group.MapPost("/links/{linkId:guid}/opening-balance-suggestion", ApplyOpeningBalance).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapPost("/links/{linkId:guid}/card-schedule-suggestion", ApplyCardSchedule).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapPost("/reprocess", Reprocess).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapGet("/review", ListReview);
        group.MapPost("/review/{postingId:guid}/resolve", Resolve).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapGet("/categorization", CategorizationOverview);
        group.MapPost("/categorization/run", RunCategorization).AddEndpointFilter<ValidateCsrfTokenFilter>();
        return app;
    }

    private static async Task<IResult> Status(Guid workspaceId, CurrentUser user, PluggyAccountLinkService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized()
        : await service.CanAccessAsync(workspaceId, userId, ct) ? Results.Ok(new { enabled = true }) : Results.NotFound();

    private static async Task<IResult> ListConnections(Guid workspaceId, CurrentUser user, PluggyPocService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized()
        : PluggyPocEndpoints.Map(await service.ListConnectionsAsync(workspaceId, userId, ct), Results.Ok);

    private static async Task<IResult> ListAvailable(Guid workspaceId, Guid connectionId, CurrentUser user, PluggyAccountLinkService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized()
        : PluggyPocEndpoints.Map(await service.ListAvailableAsync(workspaceId, userId, connectionId, ct), Results.Ok);

    private static async Task<IResult> ListLinks(Guid workspaceId, CurrentUser user, PluggyAccountLinkService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized()
        : PluggyPocEndpoints.Map(await service.ListAsync(workspaceId, userId, ct), Results.Ok);

    private static async Task<IResult> GetLink(Guid workspaceId, Guid linkId, CurrentUser user, PluggyAccountLinkService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized()
        : PluggyPocEndpoints.Map(await service.GetDetailAsync(workspaceId, userId, linkId, ct), Results.Ok);

    private static async Task<IResult> Link(Guid workspaceId, LinkRequest? request, CurrentUser user, PluggyAccountLinkService service, CancellationToken ct)
    {
        if (await user.GetUserIdAsync(ct) is not { } userId) return Results.Unauthorized();
        var input = new PluggyLinkRequest(request?.ConnectionId, request?.PluggyAccountId, request?.Mode, request?.ExistingAccountId, request?.ExistingCardId, request?.Name,
            request?.FinancialScope, request?.ResponsibleUserId, request?.AccountType, request?.ImportFrom, request?.ClosingDay, request?.DueDay, request?.CreditLimit,
            request?.PaymentAccountId);
        return PluggyPocEndpoints.Map(await service.LinkAsync(workspaceId, userId, input, ct),
            detail => Results.Created($"/api/financial-workspaces/{workspaceId}/pluggy-integration/links/{detail.Link.Id}", detail));
    }

    private static async Task<IResult> ApplyOpeningBalance(Guid workspaceId, Guid linkId, OpeningBalanceRequest? request, CurrentUser user, PluggyAccountLinkService service, CancellationToken ct)
    {
        if (await user.GetUserIdAsync(ct) is not { } userId) return Results.Unauthorized();
        // O valor nunca vem do navegador: o backend recalcula a sugestão. Sem confirmação explícita nada é aplicado.
        if (request?.Confirm != true) return Results.ValidationProblem(new Dictionary<string, string[]> { ["confirm"] = ["Confirme explicitamente a aplicação do saldo inicial sugerido."] });
        return PluggyPocEndpoints.Map(await service.ApplyOpeningBalanceSuggestionAsync(workspaceId, userId, linkId, request.ReplaceExisting == true, ct), Results.Ok);
    }

    private static async Task<IResult> ApplyCardSchedule(Guid workspaceId, Guid linkId, ConfirmRequest? request, CurrentUser user, PluggyAccountLinkService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized()
        : PluggyPocEndpoints.Map(await service.ApplyCardScheduleSuggestionAsync(workspaceId, userId, linkId, request?.Confirm == true, ct), Results.Ok);

    private static async Task<IResult> Reprocess(Guid workspaceId, CurrentUser user, PluggyAccountLinkService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized()
        : PluggyPocEndpoints.Map(await service.ReprocessAsync(workspaceId, userId, ct), Results.Ok);

    private static async Task<IResult> ListReview(Guid workspaceId, Guid? linkId, int? page, int? pageSize, CurrentUser user, PluggyReviewService service, CancellationToken ct)
    {
        if (await user.GetUserIdAsync(ct) is not { } userId) return Results.Unauthorized();
        if (page is < 1 || pageSize is < 1 or > PluggyReviewService.MaxPageSize)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["page"] = ["Paginação inválida."] });
        return PluggyPocEndpoints.Map(await service.ListAsync(workspaceId, userId, linkId, page ?? 1, pageSize ?? 25, ct), Results.Ok);
    }

    private static async Task<IResult> CategorizationOverview(Guid workspaceId, CurrentUser user, PluggyAccountLinkService access, IPluggyCategorizationService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized()
        : !await access.CanAccessAsync(workspaceId, userId, ct) ? Results.NotFound()
        : Results.Ok(await service.GetOverviewAsync(workspaceId, ct));

    /// <summary>
    /// Categoriza sob demanda os lançamentos gerados pela Pluggy. A etapa local (regras e categoria da Pluggy) não exige confirmação; usar a IA
    /// (envio de descrições saneadas à OpenAI) exige <c>confirmExternal: true</c> a cada chamada.
    /// </summary>
    private static async Task<IResult> RunCategorization(Guid workspaceId, CategorizationRequest? request, CurrentUser user, PluggyAccountLinkService access,
        IPluggyCategorizationService service, TimeProvider clock, CancellationToken ct)
    {
        if (await user.GetUserIdAsync(ct) is not { } userId) return Results.Unauthorized();
        if (!await access.CanAccessAsync(workspaceId, userId, ct)) return Results.NotFound();
        var run = await service.RunAsync(workspaceId, userId, new(request?.UseAi ?? false, request?.ConfirmExternal ?? false, request?.Force ?? false), clock.GetUtcNow(), ct);
        return run.Outcome switch
        {
            PluggyCategorizationOutcome.ConfirmationRequired => Results.ValidationProblem(new Dictionary<string, string[]>
                { ["confirmExternal"] = ["Confirme explicitamente o envio de descrições saneadas à OpenAI para usar a IA."] }),
            _ => Results.Ok(run.Result),
        };
    }

    private static async Task<IResult> Resolve(Guid workspaceId, Guid postingId, ResolveRequest? request, CurrentUser user, PluggyReviewService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized()
        : PluggyPocEndpoints.Map(await service.ResolveAsync(workspaceId, userId, postingId, request?.Resolution, ct), Results.Ok);
}

/// <summary>Vínculo de conta. Campos desconhecidos (workspaceId, userId...) são recusados.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LinkRequest(Guid? ConnectionId, string? PluggyAccountId, string? Mode, Guid? ExistingAccountId, Guid? ExistingCardId, string? Name, string? FinancialScope,
    Guid? ResponsibleUserId, string? AccountType, DateOnly? ImportFrom, int? ClosingDay, int? DueDay, decimal? CreditLimit, Guid? PaymentAccountId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OpeningBalanceRequest(bool? Confirm, bool? ReplaceExisting);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ResolveRequest(string? Resolution);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CategorizationRequest(bool? UseAi, bool? ConfirmExternal, bool? Force);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConfirmRequest(bool? Confirm);
