using System.Text.Json.Serialization;
using Saldo360.Api.Authentication;
using Saldo360.Application.CreditCards;

namespace Saldo360.Api.CreditCards;

public static class FinancialCreditCardImportedInstallmentEndpoints
{
    public static IEndpointRouteBuilder MapFinancialCreditCardImportedInstallmentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/families/{familyId:guid}/credit-cards/{cardId:guid}/imports/installments").RequireAuthorization();
        group.MapPost("/matches", Matches).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapPost("/link", Link).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapPost("/historical", Historical).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapGet("/", List);
        group.MapDelete("/{id:guid}", Cancel).AddEndpointFilter<ValidateCsrfTokenFilter>();
        return app;
    }

    private static async Task<IResult> Matches(Guid familyId, Guid cardId, InstallmentRequest? request, CurrentUser user, FinancialCreditCardImportedInstallmentService service, CancellationToken cancellationToken) =>
        await user.GetUserIdAsync(cancellationToken) is not { } userId ? Results.Unauthorized() : Result(await service.FindMatchesAsync(familyId, cardId, userId, request?.ToInput(), cancellationToken), matches: true);

    private static async Task<IResult> Link(Guid familyId, Guid cardId, InstallmentRequest? request, CurrentUser user, FinancialCreditCardImportedInstallmentService service, CancellationToken cancellationToken) =>
        await user.GetUserIdAsync(cancellationToken) is not { } userId ? Results.Unauthorized() : Result(await service.LinkAsync(familyId, cardId, userId, request?.ToInput(), cancellationToken));

    private static async Task<IResult> Historical(Guid familyId, Guid cardId, InstallmentRequest? request, CurrentUser user, FinancialCreditCardImportedInstallmentService service, CancellationToken cancellationToken) =>
        await user.GetUserIdAsync(cancellationToken) is not { } userId ? Results.Unauthorized() : Result(await service.CreateHistoricalAsync(familyId, cardId, userId, request?.ToInput(), cancellationToken));

    private static async Task<IResult> List(Guid familyId, Guid cardId, CurrentUser user, FinancialCreditCardImportedInstallmentService service, CancellationToken cancellationToken) =>
        await user.GetUserIdAsync(cancellationToken) is not { } userId ? Results.Unauthorized() : await service.ListAsync(familyId, cardId, userId, cancellationToken) is { } list ? Results.Ok(list) : Results.NotFound();

    private static async Task<IResult> Cancel(Guid familyId, Guid cardId, Guid id, CurrentUser user, FinancialCreditCardImportedInstallmentService service, CancellationToken cancellationToken) =>
        await user.GetUserIdAsync(cancellationToken) is not { } userId ? Results.Unauthorized() : await service.CancelAsync(familyId, cardId, id, userId, cancellationToken) ? Results.NoContent() : Results.Conflict(new { message = "A obrigação não existe, não é histórica ou não pode ser cancelada após pagamentos." });

    private static IResult Result(ImportedInstallmentOperationResult result, bool matches = false) => result.Access switch
    {
        ImportedInstallmentAccess.NotFound => Results.NotFound(),
        ImportedInstallmentAccess.Invalid => Results.ValidationProblem(result.Errors!),
        ImportedInstallmentAccess.Conflict => Results.Conflict(new { message = result.Message }),
        _ => matches ? Results.Ok(result.Matches) : Results.Ok(result.Record),
    };

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record InstallmentRequest(Guid ImportId, int RowNumber, Guid? InstallmentId, string? TransactionDate, string? Description, string? Amount, int? InstallmentNumber, int? InstallmentCount, string? Competence, string? ExternalId, Guid? CategoryId, bool Confirm)
    {
        public ImportedInstallmentInput ToInput() => new(ImportId, RowNumber, InstallmentId, TransactionDate ?? string.Empty, Description ?? string.Empty, Amount ?? string.Empty, InstallmentNumber ?? 0, InstallmentCount ?? 0, Competence, ExternalId, CategoryId, Confirm);
    }
}
