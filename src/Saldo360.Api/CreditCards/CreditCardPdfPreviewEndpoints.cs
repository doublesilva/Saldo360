using Saldo360.Api.Authentication;
using Saldo360.Application.CreditCards;
using System.Text.Json;

namespace Saldo360.Api.CreditCards;

public static class CreditCardPdfPreviewEndpoints
{
    public static IEndpointRouteBuilder MapCreditCardPdfPreviewEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/financial-workspaces/{workspaceId:guid}/credit-cards/{cardId:guid}/imports/pdf").RequireAuthorization();
        group.MapPost("/preview", Preview).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapPost("/agent-preview", AgentPreview).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapPost("/agent-shadow-preview", AgentShadowPreview).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapPost("/pre-reconcile", PreReconcile).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapPost("/confirm", Confirm).AddEndpointFilter<ValidateCsrfTokenFilter>();
        return app;
    }

    private static async Task<IResult> AgentShadowPreview(Guid workspaceId, Guid cardId, HttpRequest request, CurrentUser user, ICreditCardStatementAgentShadowService service, CancellationToken cancellationToken)
    {
        if (await user.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        if (!request.HasFormContentType) return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["Envie o arquivo usando multipart/form-data."] });
        var form = await request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["Informe um arquivo PDF."] });
        await using var stream = file.OpenReadStream();
        var result = await service.PreviewAsync(workspaceId, cardId, userId, stream, file.Length, file.FileName,
            form["competence"], form["password"], cancellationToken);
        return result.Access switch
        {
            CreditCardPdfPreviewAccess.NotFound => Results.NotFound(),
            CreditCardPdfPreviewAccess.Invalid => Results.ValidationProblem(result.Errors!),
            _ => Results.Ok(result.Preview),
        };
    }

    private static async Task<IResult> AgentPreview(Guid workspaceId, Guid cardId, HttpRequest request, CurrentUser user, ICreditCardStatementAgentPreviewService service, CancellationToken cancellationToken)
    {
        if (await user.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        if (!request.HasFormContentType) return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["Envie o arquivo usando multipart/form-data."] });
        var form = await request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["Informe um arquivo PDF."] });
        await using var stream = file.OpenReadStream();
        var result = await service.PreviewAsync(workspaceId, cardId, userId, stream, file.Length, file.FileName,
            form["competence"], form["password"], cancellationToken);
        return result.Access switch
        {
            CreditCardPdfPreviewAccess.NotFound => Results.NotFound(),
            CreditCardPdfPreviewAccess.Invalid => Results.ValidationProblem(result.Errors!),
            _ => Results.Ok(result.Preview),
        };
    }

    private static async Task<IResult> Preview(Guid workspaceId, Guid cardId, HttpRequest request, CurrentUser user, ICreditCardPdfPreviewService service, CancellationToken cancellationToken)
    {
        if (await user.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        if (!request.HasFormContentType) return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["Envie o arquivo usando multipart/form-data."] });
        var form = await request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["Informe um arquivo PDF."] });
        await using var stream = file.OpenReadStream();
        var result = await service.PreviewAsync(workspaceId, cardId, userId, stream, file.Length, file.FileName, form["issuerProfile"], form["competence"], form["dateFormat"], form["password"], cancellationToken);
        return result.Access switch
        {
            CreditCardPdfPreviewAccess.NotFound => Results.NotFound(),
            CreditCardPdfPreviewAccess.Invalid => Results.ValidationProblem(result.Errors!),
            _ => Results.Ok(result.Preview)
        };
    }

    private static async Task<IResult> PreReconcile(Guid workspaceId, Guid cardId, HttpRequest request, CurrentUser user, ICreditCardPdfPreReconciliationService service, CancellationToken cancellationToken)
    {
        if (await user.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        if (!request.HasFormContentType) return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["Envie o arquivo usando multipart/form-data."] });
        var form = await request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["Informe um arquivo PDF."] });
        await using var stream = file.OpenReadStream();
        var result = await service.PreReconcileAsync(workspaceId, cardId, userId, stream, file.Length, file.FileName,
            form["issuerProfile"], form["competence"], form["dateFormat"], form["password"], cancellationToken);
        return result.Access switch
        {
            CreditCardPdfPreviewAccess.NotFound => Results.NotFound(),
            CreditCardPdfPreviewAccess.Invalid => Results.ValidationProblem(result.Errors!),
            _ => Results.Ok(result.Report)
        };
    }

    private static async Task<IResult> Confirm(Guid workspaceId, Guid cardId, HttpRequest request, CurrentUser user, CreditCardPdfConfirmationService service, CancellationToken cancellationToken)
    {
        if (await user.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        if (!request.HasFormContentType) return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["Envie o arquivo usando multipart/form-data."] });
        var form = await request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["Informe um arquivo PDF."] });
        IReadOnlyList<CreditCardPdfCategoryDecision> categories;
        try
        {
            categories = string.IsNullOrWhiteSpace(form["categories"])
                ? []
                : JsonSerializer.Deserialize<List<CreditCardPdfCategoryDecision>>(form["categories"]!, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        }
        catch (JsonException)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["categories"] = ["Informe decisões de categoria em JSON válido."] });
        }
        var input = new CreditCardPdfConfirmationInput(
            bool.TryParse(form["confirm"], out var confirm) && confirm,
            form["documentId"].ToString(), categories);
        await using var stream = file.OpenReadStream();
        var result = await service.ConfirmAsync(workspaceId, cardId, userId, stream, file.Length, file.FileName,
            form["issuerProfile"], form["competence"], form["dateFormat"], form["password"], input, cancellationToken);
        return result.Access switch
        {
            CreditCardPdfConfirmationAccess.NotFound => Results.NotFound(),
            CreditCardPdfConfirmationAccess.Invalid => Results.ValidationProblem(result.Errors!),
            CreditCardPdfConfirmationAccess.Conflict => Results.Conflict(new { message = result.Message }),
            _ => Results.Ok(result.Result)
        };
    }
}
