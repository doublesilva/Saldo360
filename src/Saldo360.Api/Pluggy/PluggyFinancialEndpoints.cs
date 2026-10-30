using System.Text.Json.Serialization;
using Saldo360.Api.Authentication;
using Saldo360.Application.Classification;
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
        group.MapGet("/sync-status", SyncStatus);
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
        // R04: central de automação (preferências, estado e histórico). A leitura nunca dispara execução.
        group.MapGet("/automation", AutomationOverview);
        group.MapPut("/automation", UpdateAutomation).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapGet("/automation/runs", ListAutomationRuns);
        group.MapGet("/automation/runs/{kind}/{runId:guid}", GetAutomationRun);
        // R04: revisão da classificação (tipo de despesa e categoria) dos registros que originam as contagens, inclusive compras de cartão.
        group.MapGet("/classification-review", ClassificationReview);
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

    /// <summary>Monitoramento da sincronização por conexão (última tentativa e sucesso, próxima execução, contagens, erro resumido, consentimento). Só lê o banco: não consulta a Pluggy.</summary>
    private static async Task<IResult> SyncStatus(Guid workspaceId, CurrentUser user, PluggySyncMonitorService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized()
        : PluggyPocEndpoints.Map(await service.GetAsync(workspaceId, userId, ct), Results.Ok);

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
        PluggyCategorizationJob job, CancellationToken ct)
    {
        if (await user.GetUserIdAsync(ct) is not { } userId) return Results.Unauthorized();
        if (!await access.CanAccessAsync(workspaceId, userId, ct)) return Results.NotFound();
        var run = await job.RunAsync(workspaceId, userId, Saldo360.Domain.Pluggy.PluggyCategorizationTrigger.Manual,
            new(request?.UseAi ?? false, request?.ConfirmExternal ?? false, request?.Force ?? false), null, null, ct);
        return run.Outcome switch
        {
            PluggyCategorizationJobOutcome.ConfirmationRequired => Results.ValidationProblem(new Dictionary<string, string[]>
                { ["confirmExternal"] = ["Confirme explicitamente o envio de descrições saneadas à OpenAI para usar a IA."] }),
            PluggyCategorizationJobOutcome.Busy => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Já existe uma categorização em andamento neste orçamento."),
            PluggyCategorizationJobOutcome.Failed => Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "A categorização não pôde ser concluída.",
                extensions: new Dictionary<string, object?> { ["code"] = run.FailureCode }),
            _ => Results.Ok(run.Result),
        };
    }

    private static async Task<IResult> AutomationOverview(Guid workspaceId, CurrentUser user, PluggyAutomationService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized() : Automation(await service.GetOverviewAsync(workspaceId, userId, ct));

    private static async Task<IResult> UpdateAutomation(Guid workspaceId, AutomationSettingsRequest? request, CurrentUser user, PluggyAutomationService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized()
        : Automation(await service.UpdateAsync(workspaceId, userId, new(request?.SyncPaused, request?.SyncIntervalHours, request?.CategorizeAfterSync, request?.ConfirmExternal), ct));

    private static async Task<IResult> ListAutomationRuns(Guid workspaceId, string? kind, string? state, string? from, string? to, int? page, int? pageSize, CurrentUser user,
        PluggyAutomationService service, CancellationToken ct)
    {
        if (await user.GetUserIdAsync(ct) is not { } userId) return Results.Unauthorized();
        var errors = new Dictionary<string, string[]>();
        var start = IsoDateQuery.Parse(from, "from", errors);
        var end = IsoDateQuery.Parse(to, "to", errors);
        if (page is < 1 || pageSize is < 1 or > PluggyAutomationService.MaxPageSize) errors["page"] = ["Paginação inválida."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);
        return Automation(await service.ListRunsAsync(workspaceId, userId, new(string.IsNullOrEmpty(kind) ? null : kind, string.IsNullOrEmpty(state) ? null : state, start, end, page ?? 1, pageSize ?? 20), ct));
    }

    private static async Task<IResult> GetAutomationRun(Guid workspaceId, string kind, Guid runId, CurrentUser user, PluggyAutomationService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized() : Automation(await service.GetRunAsync(workspaceId, userId, kind, runId, ct));

    private static async Task<IResult> ClassificationReview(Guid workspaceId, string? kind, string? from, string? to, int? page, int? pageSize, CurrentUser user,
        ClassificationReviewService service, TimeProvider clock, CancellationToken ct)
    {
        if (await user.GetUserIdAsync(ct) is not { } userId) return Results.Unauthorized();
        var errors = new Dictionary<string, string[]>();
        var start = IsoDateQuery.Parse(from, "from", errors);
        var end = IsoDateQuery.Parse(to, "to", errors);
        if (page is < 1 || pageSize is < 1 or > ClassificationReviewService.MaxPageSize) errors["page"] = ["Paginação inválida."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);
        var result = await service.ListAsync(workspaceId, userId, string.IsNullOrEmpty(kind) ? null : kind, start, end, page ?? 1, pageSize ?? 25, Saldo360.Domain.Common.BusinessCalendar.Today(clock.GetUtcNow()), ct);
        return result.Access switch
        {
            ClassificationReviewAccess.Allowed => Results.Ok(ClassificationReviewResponse.Of(result.Page!)),
            ClassificationReviewAccess.Invalid => Results.ValidationProblem(result.Errors!),
            _ => Results.NotFound(),
        };
    }

    private static IResult Automation<T>(PluggyAutomationResult<T> result) => result.Access switch
    {
        PluggyAutomationAccess.Allowed => Results.Ok(result.Value),
        PluggyAutomationAccess.Invalid => Results.ValidationProblem(result.Errors!),
        _ => Results.NotFound(),
    };

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

public sealed record ClassificationReviewItemResponse(ClassificationAction Action, Guid RecordId, Guid? CardId, DateOnly Date, string Description, string Amount, Guid? CategoryId, string? CategoryPath,
    Saldo360.Domain.Categories.ExpenseNature ExpenseNature, string? CardName, string? AccountName, bool Imported, bool CanChangeCategory, bool CanChangeNature, string Scope);

public sealed record ClassificationReviewResponse(string Kind, DateOnly? From, DateOnly? To, bool WholeBudget, IReadOnlyList<ClassificationReviewItemResponse> Items, int Total, int Page, int PageSize, int UnclassifiableCount)
{
    public static ClassificationReviewResponse Of(ClassificationReviewPage page) => new(page.Kind, page.From, page.To, page.WholeBudget,
        page.Items.Select(i => new ClassificationReviewItemResponse(i.Action, i.RecordId, i.CardId, i.Date, i.Description, Saldo360.Domain.Common.Money.Format(i.Amount), i.CategoryId, i.CategoryPath, i.Nature,
            i.CardName, i.AccountName, i.Imported, i.CanChangeCategory, i.CanChangeNature, i.Scope)).ToList(), page.Total, page.Page, page.PageSize, page.UnclassifiableCount);
}

/// <summary>Preferências de automação do orçamento. Qualquer outro campo é recusado.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AutomationSettingsRequest(bool? SyncPaused, int? SyncIntervalHours, bool? CategorizeAfterSync, bool? ConfirmExternal);
