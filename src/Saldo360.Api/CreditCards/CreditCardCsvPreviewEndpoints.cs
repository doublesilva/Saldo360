using Saldo360.Api.Authentication;
using Saldo360.Application.CreditCards;

namespace Saldo360.Api.CreditCards;

public static class CreditCardCsvPreviewEndpoints
{
    public static IEndpointRouteBuilder MapCreditCardCsvPreviewEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/families/{familyId:guid}/credit-cards/{cardId:guid}/imports").RequireAuthorization();
        group.MapPost("/preview", Preview).AddEndpointFilter<ValidateCsrfTokenFilter>();
        return app;
    }

    private static async Task<IResult> Preview(
        Guid familyId,
        Guid cardId,
        HttpRequest request,
        CurrentUser user,
        ICreditCardCsvPreviewService service,
        CancellationToken cancellationToken)
    {
        if (await user.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        if (!request.HasFormContentType) return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["Envie o arquivo usando multipart/form-data."] });
        var form = await request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file is null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["Informe um arquivo CSV."] });
        if (!file.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["O arquivo deve ter extensão .csv."] });
        if (file.Length == 0) return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["O arquivo CSV está vazio."] });

        var input = new CreditCardCsvPreviewRequest(
            form["dateColumn"], form["descriptionColumn"], form["amountColumn"], form["categoryColumn"],
            form["installmentNumberColumn"], form["installmentCountColumn"], form["externalIdColumn"], form["competenceColumn"],
            form["delimiter"], form["dateFormat"], form["decimalFormat"]);
        await using var stream = file.OpenReadStream();
        var result = await service.PreviewAsync(familyId, cardId, userId, stream, file.Length, input, cancellationToken);
        return result.Access switch
        {
            CreditCardCsvPreviewAccess.NotFound => Results.NotFound(),
            CreditCardCsvPreviewAccess.Invalid => Results.ValidationProblem(result.Errors!),
            _ => Results.Ok(result.Preview),
        };
    }
}
