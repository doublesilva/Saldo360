using System.Text.Json.Serialization;
using Saldo360.Api.Authentication;
using Saldo360.Application.CreditCards;

namespace Saldo360.Api.CreditCards;

public static class FinancialCreditCardImportEndpoints
{
    public static IEndpointRouteBuilder MapFinancialCreditCardImportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/financial-workspaces/{workspaceId:guid}/credit-cards/{cardId:guid}/imports").RequireAuthorization();
        group.MapPost("/confirm", Confirm).AddEndpointFilter<ValidateCsrfTokenFilter>();
        return app;
    }

    private static async Task<IResult> Confirm(Guid workspaceId, Guid cardId, ConfirmationRequest? request, CurrentUser user, FinancialCreditCardImportService service, CancellationToken cancellationToken)
    {
        if (await user.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        var input = request is null ? null : new CreditCardImportConfirmation(request.ImportId, request.Confirm, request.Items?.Select(item => item.ToInput()).ToList());
        var result = await service.ConfirmAsync(workspaceId, cardId, userId, input, cancellationToken);
        return result.Access switch
        {
            CreditCardImportAccess.NotFound => Results.NotFound(),
            CreditCardImportAccess.Invalid => Results.ValidationProblem(result.Errors!),
            CreditCardImportAccess.Conflict => Results.Conflict(new { message = result.Message }),
            _ => Results.Ok(result.Result),
        };
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record ConfirmationRequest(Guid ImportId, bool Confirm, IReadOnlyList<ItemRequest>? Items);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record ItemRequest(int RowNumber, string? TransactionDate, string? Description, string? Amount, Guid CategoryId, int? InstallmentNumber, int? InstallmentCount, string? TotalAmount, string? ExternalId, string? Competence)
    {
        public CreditCardImportItemInput ToInput() => new(RowNumber, TransactionDate ?? string.Empty, Description ?? string.Empty, Amount ?? string.Empty, CategoryId, InstallmentNumber, InstallmentCount, TotalAmount, ExternalId, Competence);
    }
}
