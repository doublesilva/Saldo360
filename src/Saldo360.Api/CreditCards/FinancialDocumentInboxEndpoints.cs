using Saldo360.Api.Authentication;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.CreditCards;
using System.Text.Json;

namespace Saldo360.Api.CreditCards;

public static class FinancialDocumentInboxEndpoints
{
    public static IEndpointRouteBuilder MapFinancialDocumentInboxEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/families/{familyId:guid}/financial-documents/inbox").RequireAuthorization();
        group.MapGet("/", List);
        group.MapPost("/", Receive).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapPost("/{id:guid}/reanalyze", Reanalyze).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapPost("/{id:guid}/classify-preview", ClassifyPreview).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapPost("/{id:guid}/service-bill-preview", ServiceBillPreview).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapPost("/{id:guid}/pre-reconcile", PreReconcile).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapPost("/{id:guid}/ignore", Ignore).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapPost("/{id:guid}/confirm", Confirm).AddEndpointFilter<ValidateCsrfTokenFilter>();
        return app;
    }

    private static async Task<IResult> ServiceBillPreview(Guid familyId, Guid id, HttpRequest request, CurrentUser user,
        ServiceBillPreviewService service, CancellationToken cancellationToken)
    {
        if (await user.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        IFormCollection? form = request.HasFormContentType ? await request.ReadFormAsync(cancellationToken) : null;
        var result = await service.PreviewAsync(familyId, id, userId, form?["password"], cancellationToken);
        return result.Access switch { FinancialDocumentClassificationAccess.NotFound => Results.NotFound(),
            FinancialDocumentClassificationAccess.Invalid => Results.ValidationProblem(result.Errors!), _ => Results.Ok(result.Preview) };
    }

    private static async Task<IResult> ClassifyPreview(Guid familyId, Guid id, HttpRequest request, CurrentUser user,
        FinancialDocumentClassificationPreviewService service, CancellationToken cancellationToken)
    {
        if (await user.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        IFormCollection? form = request.HasFormContentType ? await request.ReadFormAsync(cancellationToken) : null;
        var result = await service.PreviewAsync(familyId, id, userId, form?["password"], cancellationToken);
        return result.Access switch
        {
            FinancialDocumentClassificationAccess.NotFound => Results.NotFound(),
            FinancialDocumentClassificationAccess.Invalid => Results.ValidationProblem(result.Errors!),
            _ => Results.Ok(result.Classification)
        };
    }

    private static async Task<IResult> PreReconcile(Guid familyId, Guid id, HttpRequest request, CurrentUser user,
        FinancialDocumentInboxReviewService service, CancellationToken cancellationToken)
    {
        if (await user.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        if (!request.HasFormContentType) return Validation("request", "Envie a revisão usando multipart/form-data.");
        var form = await request.ReadFormAsync(cancellationToken);
        if (!Guid.TryParse(form["creditCardId"], out var cardId)) return Validation("creditCardId", "Informe o cartão selecionado.");
        var result = await service.ReviewAsync(familyId, id, cardId, userId, form["password"], cancellationToken);
        return result.Access switch
        {
            CreditCardPdfPreviewAccess.NotFound => Results.NotFound(),
            CreditCardPdfPreviewAccess.Invalid => Results.ValidationProblem(result.Errors!),
            _ => Results.Ok(result.Report)
        };
    }

    private static async Task<IResult> List(Guid familyId, CurrentUser user, FinancialDocumentInboxService service, CancellationToken cancellationToken)
    {
        if (await user.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        return await service.ListAsync(familyId, userId, cancellationToken) is { } items ? Results.Ok(items) : Results.NotFound();
    }

    private static async Task<IResult> Receive(Guid familyId, HttpRequest request, CurrentUser user, FinancialDocumentInboxService service, CancellationToken cancellationToken)
    {
        if (await user.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        if (!request.HasFormContentType) return Validation("file", "Envie o arquivo usando multipart/form-data.");
        var form = await request.ReadFormAsync(cancellationToken); var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null) return Validation("file", "Informe um arquivo PDF.");
        var cardId = Guid.TryParse(form["creditCardId"], out var parsedCardId) ? parsedCardId : (Guid?)null;
        await using var stream = file.OpenReadStream();
        var result = await service.ReceiveAsync(familyId, userId, stream, file.Length, file.FileName, file.ContentType,
            FinancialDocumentSource.Manual, form["externalSourceId"], cardId, form["password"], cancellationToken);
        return result.Result switch { "NotFound" => Results.NotFound(), "Invalid" => Results.ValidationProblem(result.Errors!),
            "Conflict" => Results.Conflict(result.Item), "Created" => Results.Created($"/api/families/{familyId}/financial-documents/inbox/{result.Item!.Id}", result.Item), _ => Results.Ok(result.Item) };
    }

    private static async Task<IResult> Reanalyze(Guid familyId, Guid id, HttpRequest request, CurrentUser user, FinancialDocumentInboxService service, CancellationToken cancellationToken)
    {
        if (await user.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        IFormCollection? form = request.HasFormContentType ? await request.ReadFormAsync(cancellationToken) : null;
        var cardId = Guid.TryParse(form?["creditCardId"], out var parsedCardId) ? parsedCardId : (Guid?)null;
        return await service.ReprocessAsync(familyId, id, userId, cardId, form?["password"], cancellationToken) is { } item ? Results.Ok(item) : Results.NotFound();
    }

    private static async Task<IResult> Ignore(Guid familyId, Guid id, CurrentUser user, FinancialDocumentInboxService service, CancellationToken cancellationToken)
    {
        if (await user.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        return await service.IgnoreAsync(familyId, id, userId, cancellationToken) is { } item ? Results.Ok(item) : Results.NotFound();
    }
    private static async Task<IResult> Confirm(Guid familyId, Guid id, HttpRequest request, CurrentUser user, FinancialDocumentInboxConfirmationService service, CancellationToken cancellationToken)
    {
        if (await user.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        if (!request.HasFormContentType) return Validation("request", "Envie a confirmação usando multipart/form-data.");
        var form = await request.ReadFormAsync(cancellationToken);
        if (!Guid.TryParse(form["creditCardId"], out var cardId)) return Validation("creditCardId", "Informe o cartão selecionado.");
        IReadOnlyList<CreditCardPdfCategoryDecision> categories;
        try { categories = string.IsNullOrWhiteSpace(form["categories"]) ? [] : JsonSerializer.Deserialize<List<CreditCardPdfCategoryDecision>>(form["categories"]!, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? []; }
        catch (JsonException) { return Validation("categories", "Informe decisões de categoria em JSON válido."); }
        var input = new CreditCardPdfConfirmationInput(bool.TryParse(form["confirm"], out var confirm) && confirm, form["documentId"]!, categories);
        var result = await service.ConfirmAsync(familyId, id, cardId, userId, form["password"], input, cancellationToken);
        return result.Access switch { CreditCardPdfConfirmationAccess.NotFound => Results.NotFound(),
            CreditCardPdfConfirmationAccess.Invalid => Results.ValidationProblem(result.Errors!),
            CreditCardPdfConfirmationAccess.Conflict => Results.Conflict(new { message = result.Message }), _ => Results.Ok(result.Result) };
    }
    private static IResult Validation(string key, string value) => Results.ValidationProblem(new Dictionary<string, string[]> { [key] = [value] });
}
