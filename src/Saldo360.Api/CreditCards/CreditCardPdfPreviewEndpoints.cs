using Saldo360.Api.Authentication;
using Saldo360.Application.CreditCards;

namespace Saldo360.Api.CreditCards;

public static class CreditCardPdfPreviewEndpoints
{
    public static IEndpointRouteBuilder MapCreditCardPdfPreviewEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/families/{familyId:guid}/credit-cards/{cardId:guid}/imports/pdf").RequireAuthorization();
        group.MapPost("/preview", Preview).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapPost("/pre-reconcile", PreReconcile).AddEndpointFilter<ValidateCsrfTokenFilter>();
        return app;
    }

    private static async Task<IResult> Preview(Guid familyId, Guid cardId, HttpRequest request, CurrentUser user, ICreditCardPdfPreviewService service, CancellationToken cancellationToken)
    {
        if (await user.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        if (!request.HasFormContentType) return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["Envie o arquivo usando multipart/form-data."] });
        var form = await request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["Informe um arquivo PDF."] });
        await using var stream = file.OpenReadStream();
        var result = await service.PreviewAsync(familyId, cardId, userId, stream, file.Length, file.FileName, form["issuerProfile"], form["competence"], form["dateFormat"], form["password"], cancellationToken);
        return result.Access switch
        {
            CreditCardPdfPreviewAccess.NotFound => Results.NotFound(),
            CreditCardPdfPreviewAccess.Invalid => Results.ValidationProblem(result.Errors!),
            _ => Results.Ok(result.Preview)
        };
    }

    private static async Task<IResult> PreReconcile(Guid familyId, Guid cardId, HttpRequest request, CurrentUser user, ICreditCardPdfPreReconciliationService service, CancellationToken cancellationToken)
    {
        if (await user.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        if (!request.HasFormContentType) return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["Envie o arquivo usando multipart/form-data."] });
        var form = await request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["Informe um arquivo PDF."] });
        await using var stream = file.OpenReadStream();
        var result = await service.PreReconcileAsync(familyId, cardId, userId, stream, file.Length, file.FileName,
            form["issuerProfile"], form["competence"], form["dateFormat"], form["password"], cancellationToken);
        return result.Access switch
        {
            CreditCardPdfPreviewAccess.NotFound => Results.NotFound(),
            CreditCardPdfPreviewAccess.Invalid => Results.ValidationProblem(result.Errors!),
            _ => Results.Ok(result.Report)
        };
    }
}
