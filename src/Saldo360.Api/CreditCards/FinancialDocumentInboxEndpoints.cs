using Saldo360.Api.Authentication;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.CreditCards;
using System.Text.Json;

namespace Saldo360.Api.CreditCards;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record StatementCategoryCorrection(Guid CategoryId);

public static class FinancialDocumentInboxEndpoints
{
    public static IEndpointRouteBuilder MapFinancialDocumentInboxEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/financial-workspaces/{workspaceId:guid}/financial-documents/inbox").RequireAuthorization();
        group.MapGet("/", List);
        group.MapGet("/{id:guid}", Get);
        group.MapGet("/{id:guid}/statement-analysis", GetStatementAnalysis);
        group.MapPost("/{id:guid}/statement-analysis", AnalyzeStatement).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapPost("/{id:guid}/statement-consolidation", ConsolidateStatement).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapPost("/{id:guid}/statement-processing", ProcessStatement).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapPut("/{id:guid}/statement-analysis/items/{key}/category", CorrectStatementCategory).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapPost("/", Receive).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapPost("/{id:guid}/reanalyze", Reanalyze).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapPost("/{id:guid}/classify-preview", ClassifyPreview).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapPost("/{id:guid}/service-bill-preview", ServiceBillPreview).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapPost("/{id:guid}/service-bill-match-preview", ServiceBillMatchPreview).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapPost("/{id:guid}/service-bill-confirm", ServiceBillConfirm).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapPost("/{id:guid}/service-account-binding", ServiceAccountBinding).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapGet("/{id:guid}/service-account-binding", GetServiceAccountBinding);
        group.MapPost("/{id:guid}/pre-reconcile", PreReconcile).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapPost("/{id:guid}/ignore", Ignore).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapPost("/{id:guid}/confirm", Confirm).AddEndpointFilter<ValidateCsrfTokenFilter>();
        return app;
    }
    private static IResult AnalysisResponse(InboxStatementAnalysisResult result) => result.Status switch
    {
        "NotFound" => Results.NotFound(), "Invalid" => Results.ValidationProblem(result.Errors!),
        "Conflict" or "Busy" => Results.Conflict(result), _ => Results.Ok(result)
    };
    private static async Task<IResult> ConsolidateStatement(Guid workspaceId, Guid id, HttpRequest request, CurrentUser user, InboxStatementConsolidationService service, CancellationToken ct)
    {
        if (await user.GetUserIdAsync(ct) is not { } userId) return Results.Unauthorized();
        if (!request.HasFormContentType || !Guid.TryParse((await request.ReadFormAsync(ct))["creditCardId"], out var card)) return Validation("creditCardId", "Selecione explicitamente o cartão.");
        var result = await service.ConsolidateAsync(workspaceId, card, id, userId, ct);
        return result.Status == "NotFound" ? Results.NotFound() : Results.Ok(result);
    }
    private static async Task<IResult> ProcessStatement(Guid workspaceId, Guid id, HttpRequest request, CurrentUser user,
        InboxStatementProcessingService processing, CancellationToken ct)
    {
        if (await user.GetUserIdAsync(ct) is not { } userId) return Results.Unauthorized();
        if (!request.HasFormContentType) return Validation("creditCardId", "Selecione explicitamente o cartão.");
        var form = await request.ReadFormAsync(ct);
        if (!Guid.TryParse(form["creditCardId"], out var card)) return Validation("creditCardId", "Selecione explicitamente o cartão.");
        var result = await processing.ProcessAsync(workspaceId, id, card, userId, form["password"], ct);
        return result.Analysis.Status == "NotFound" ? Results.NotFound() : Results.Ok(result);
    }
    private static async Task<IResult> GetStatementAnalysis(Guid workspaceId, Guid id, CurrentUser user, InboxStatementAnalysisService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized() : AnalysisResponse(await service.GetAsync(workspaceId, id, userId, ct));
    private static async Task<IResult> AnalyzeStatement(Guid workspaceId, Guid id, HttpRequest request, CurrentUser user, InboxStatementAnalysisService service, CancellationToken ct)
    {
        if (await user.GetUserIdAsync(ct) is not { } userId) return Results.Unauthorized();
        if (!request.HasFormContentType) return Validation("creditCardId", "Selecione explicitamente o cartão.");
        var form = await request.ReadFormAsync(ct);
        if (!Guid.TryParse(form["creditCardId"], out var cardId)) return Validation("creditCardId", "Selecione explicitamente o cartão.");
        return AnalysisResponse(await service.AnalyzeAsync(workspaceId, id, cardId, userId, form["password"], ct));
    }
    private static async Task<IResult> CorrectStatementCategory(Guid workspaceId, Guid id, string key, StatementCategoryCorrection request,
        CurrentUser user, InboxStatementAnalysisService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized() : AnalysisResponse(await service.CorrectCategoryAsync(workspaceId, id, key, request.CategoryId, userId, ct));

    private static async Task<IResult> Get(Guid workspaceId, Guid id, CurrentUser user, FinancialDocumentInboxService service, CancellationToken ct)
    {
        if (await user.GetUserIdAsync(ct) is not { } userId) return Results.Unauthorized();
        return await service.GetAsync(workspaceId, id, userId, ct) is { } item ? Results.Ok(item) : Results.NotFound();
    }

    private static async Task<IResult> GetServiceAccountBinding(Guid workspaceId, Guid id, CurrentUser user,
        ServiceAccountBindingService service, CancellationToken cancellationToken)
    {
        if (await user.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        return await service.GetAsync(workspaceId, id, userId, cancellationToken) is { } result ? Results.Ok(result) : Results.NotFound();
    }
    private static async Task<IResult> ServiceAccountBinding(Guid workspaceId, Guid id, HttpRequest request, CurrentUser user,
        ServiceAccountBindingService service, CancellationToken cancellationToken)
    {
        if (await user.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        IFormCollection? form = request.HasFormContentType ? await request.ReadFormAsync(cancellationToken) : null;
        var result = await service.CreateAsync(workspaceId, id, userId, form?["password"], cancellationToken);
        if (result.Access == FinancialDocumentClassificationAccess.NotFound) return Results.NotFound();
        if (result.Access == FinancialDocumentClassificationAccess.Invalid) return Results.ValidationProblem(result.Errors!);
        return result.Result?.Status == "Conflict"
            ? Results.Conflict(new { message = "A referência já está vinculada a outra recorrência.", result = result.Result })
            : Results.Ok(result.Result);
    }
    private static async Task<IResult> ServiceBillConfirm(Guid workspaceId, Guid id, HttpRequest request, CurrentUser user,
        ServiceBillConfirmationService service, CancellationToken cancellationToken)
    {
        if (await user.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        IFormCollection? form = request.HasFormContentType ? await request.ReadFormAsync(cancellationToken) : null;
        var result = await service.ConfirmAsync(workspaceId, id, userId, form?["password"], cancellationToken);
        if (result.Access == FinancialDocumentClassificationAccess.NotFound) return Results.NotFound();
        if (result.Access == FinancialDocumentClassificationAccess.Invalid) return Results.ValidationProblem(result.Errors!);
        return result.Result?.Status == "Conflict"
            ? Results.Conflict(new { message = "O documento ou a ocorrência já possui confirmação conflitante.", result = result.Result })
            : Results.Ok(result.Result);
    }
    private static async Task<IResult> ServiceBillMatchPreview(Guid workspaceId, Guid id, HttpRequest request, CurrentUser user,
        ServiceBillRecurringMatchPreviewService service, CancellationToken cancellationToken)
    {
        if (await user.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        IFormCollection? form = request.HasFormContentType ? await request.ReadFormAsync(cancellationToken) : null;
        var result = await service.PreviewAsync(workspaceId, id, userId, form?["password"], cancellationToken);
        return result.Access switch { FinancialDocumentClassificationAccess.NotFound => Results.NotFound(),
            FinancialDocumentClassificationAccess.Invalid => Results.ValidationProblem(result.Errors!), _ => Results.Ok(result.Preview) };
    }

    private static async Task<IResult> ServiceBillPreview(Guid workspaceId, Guid id, HttpRequest request, CurrentUser user,
        ServiceBillPreviewService service, CancellationToken cancellationToken)
    {
        if (await user.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        IFormCollection? form = request.HasFormContentType ? await request.ReadFormAsync(cancellationToken) : null;
        var result = await service.PreviewAsync(workspaceId, id, userId, form?["password"], cancellationToken);
        return result.Access switch { FinancialDocumentClassificationAccess.NotFound => Results.NotFound(),
            FinancialDocumentClassificationAccess.Invalid => Results.ValidationProblem(result.Errors!), _ => Results.Ok(result.Preview) };
    }

    private static async Task<IResult> ClassifyPreview(Guid workspaceId, Guid id, HttpRequest request, CurrentUser user,
        FinancialDocumentClassificationPreviewService service, CancellationToken cancellationToken)
    {
        if (await user.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        IFormCollection? form = request.HasFormContentType ? await request.ReadFormAsync(cancellationToken) : null;
        var result = await service.PreviewAsync(workspaceId, id, userId, form?["password"], cancellationToken);
        return result.Access switch
        {
            FinancialDocumentClassificationAccess.NotFound => Results.NotFound(),
            FinancialDocumentClassificationAccess.Invalid => Results.ValidationProblem(result.Errors!),
            _ => Results.Ok(result.Classification)
        };
    }

    private static async Task<IResult> PreReconcile(Guid workspaceId, Guid id, HttpRequest request, CurrentUser user,
        FinancialDocumentInboxReviewService service, CancellationToken cancellationToken)
    {
        if (await user.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        if (!request.HasFormContentType) return Validation("request", "Envie a revisão usando multipart/form-data.");
        var form = await request.ReadFormAsync(cancellationToken);
        if (!Guid.TryParse(form["creditCardId"], out var cardId)) return Validation("creditCardId", "Informe o cartão selecionado.");
        var result = await service.ReviewAsync(workspaceId, id, cardId, userId, form["password"], cancellationToken);
        return result.Access switch
        {
            CreditCardPdfPreviewAccess.NotFound => Results.NotFound(),
            CreditCardPdfPreviewAccess.Invalid => Results.ValidationProblem(result.Errors!),
            _ => Results.Ok(result.Report)
        };
    }

    private static async Task<IResult> List(Guid workspaceId, CurrentUser user, FinancialDocumentInboxService service, CancellationToken cancellationToken)
    {
        if (await user.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        return await service.ListAsync(workspaceId, userId, cancellationToken) is { } items ? Results.Ok(items) : Results.NotFound();
    }

    private static async Task<IResult> Receive(Guid workspaceId, HttpRequest request, CurrentUser user, FinancialDocumentInboxService service, CancellationToken cancellationToken)
    {
        if (await user.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        if (!request.HasFormContentType) return Validation("file", "Envie o arquivo usando multipart/form-data.");
        var form = await request.ReadFormAsync(cancellationToken); var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null) return Validation("file", "Informe um arquivo PDF.");
        var cardId = Guid.TryParse(form["creditCardId"], out var parsedCardId) ? parsedCardId : (Guid?)null;
        await using var stream = file.OpenReadStream();
        var result = await service.ReceiveAsync(workspaceId, userId, stream, file.Length, file.FileName, file.ContentType,
            FinancialDocumentSource.Manual, form["externalSourceId"], cardId, form["password"], cancellationToken);
        return result.Result switch { "NotFound" => Results.NotFound(), "Invalid" => Results.ValidationProblem(result.Errors!),
            "Conflict" => Results.Conflict(result.Item), "Created" => Results.Created($"/api/financial-workspaces/{workspaceId}/financial-documents/inbox/{result.Item!.Id}", result.Item), _ => Results.Ok(result.Item) };
    }

    private static async Task<IResult> Reanalyze(Guid workspaceId, Guid id, HttpRequest request, CurrentUser user, FinancialDocumentInboxService service, CancellationToken cancellationToken)
    {
        if (await user.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        IFormCollection? form = request.HasFormContentType ? await request.ReadFormAsync(cancellationToken) : null;
        var cardId = Guid.TryParse(form?["creditCardId"], out var parsedCardId) ? parsedCardId : (Guid?)null;
        return await service.ReprocessAsync(workspaceId, id, userId, cardId, form?["password"], cancellationToken) is { } item ? Results.Ok(item) : Results.NotFound();
    }

    private static async Task<IResult> Ignore(Guid workspaceId, Guid id, CurrentUser user, FinancialDocumentInboxService service, CancellationToken cancellationToken)
    {
        if (await user.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        return await service.IgnoreAsync(workspaceId, id, userId, cancellationToken) is { } item ? Results.Ok(item) : Results.NotFound();
    }
    private static async Task<IResult> Confirm(Guid workspaceId, Guid id, HttpRequest request, CurrentUser user, FinancialDocumentInboxConfirmationService service, CancellationToken cancellationToken)
    {
        if (await user.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        if (!request.HasFormContentType) return Validation("request", "Envie a confirmação usando multipart/form-data.");
        var form = await request.ReadFormAsync(cancellationToken);
        if (!Guid.TryParse(form["creditCardId"], out var cardId)) return Validation("creditCardId", "Informe o cartão selecionado.");
        IReadOnlyList<CreditCardPdfCategoryDecision> categories;
        try { categories = string.IsNullOrWhiteSpace(form["categories"]) ? [] : JsonSerializer.Deserialize<List<CreditCardPdfCategoryDecision>>(form["categories"]!, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? []; }
        catch (JsonException) { return Validation("categories", "Informe decisões de categoria em JSON válido."); }
        var input = new CreditCardPdfConfirmationInput(bool.TryParse(form["confirm"], out var confirm) && confirm, form["documentId"]!, categories);
        var result = await service.ConfirmAsync(workspaceId, id, cardId, userId, form["password"], input, cancellationToken);
        return result.Access switch { CreditCardPdfConfirmationAccess.NotFound => Results.NotFound(),
            CreditCardPdfConfirmationAccess.Invalid => Results.ValidationProblem(result.Errors!),
            CreditCardPdfConfirmationAccess.Conflict => Results.Conflict(new { message = result.Message }), _ => Results.Ok(result.Result) };
    }
    private static IResult Validation(string key, string value) => Results.ValidationProblem(new Dictionary<string, string[]> { [key] = [value] });
}
