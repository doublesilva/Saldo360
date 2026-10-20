using System.Text.Json.Serialization;
using Saldo360.Api.Authentication;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.CreditCards;

namespace Saldo360.Api.CreditCards;

public static class FinancialCreditCardPurchaseEndpoints
{
    public static IEndpointRouteBuilder MapFinancialCreditCardPurchaseEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/financial-workspaces/{workspaceId:guid}/credit-cards/{cardId:guid}/purchases").RequireAuthorization();
        group.MapPost("/", Create).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapGet("/", List);
        group.MapGet("/{purchaseId:guid}", Get);
        group.MapPost("/{purchaseId:guid}/cancel", Cancel).AddEndpointFilter<ValidateCsrfTokenFilter>();
        return app;
    }

    private static async Task<IResult> Create(Guid workspaceId, Guid cardId, PurchaseRequest? request, CurrentUser user, FinancialCreditCardPurchaseService service, CancellationToken ct)
    {
        if (await user.GetUserIdAsync(ct) is not { } userId) return Results.Unauthorized();
        var result = await service.CreateAsync(workspaceId, cardId, userId, request?.ToInput() ?? new(null, null, null, null, null), ct);
        return result.Access switch { CreditCardPurchaseAccess.NotFound => Results.NotFound(), CreditCardPurchaseAccess.Invalid => Results.ValidationProblem(result.Errors!), _ => Results.Created($"/api/financial-workspaces/{workspaceId}/credit-cards/{cardId}/purchases/{result.Purchase!.Id}", PurchaseResponse.From(result.Purchase)) };
    }

    private static async Task<IResult> List(Guid workspaceId, Guid cardId, string? from, string? through, CurrentUser user, FinancialCreditCardPurchaseService service, CancellationToken ct)
    {
        if (await user.GetUserIdAsync(ct) is not { } userId) return Results.Unauthorized();
        var errors = new Dictionary<string, string[]>(); var start = Parse(from, "from", errors); var end = Parse(through, "through", errors);
        if (start is not null && end is not null && end < start) errors["through"] = ["through deve ser maior ou igual a from."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);
        var list = await service.ListAsync(workspaceId, cardId, userId, start, end, ct);
        return list is null ? Results.NotFound() : Results.Ok(list.Select(PurchaseResponse.From));
    }

    private static async Task<IResult> Get(Guid workspaceId, Guid cardId, Guid purchaseId, CurrentUser user, FinancialCreditCardPurchaseService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized() : await service.GetAsync(workspaceId, cardId, purchaseId, userId, ct) is { } purchase ? Results.Ok(PurchaseResponse.From(purchase)) : Results.NotFound();

    private static async Task<IResult> Cancel(Guid workspaceId, Guid cardId, Guid purchaseId, CurrentUser user, FinancialCreditCardPurchaseService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized() : await service.CancelAsync(workspaceId, cardId, purchaseId, userId, ct) ? Results.NoContent() : Results.NotFound();

    private static DateOnly? Parse(string? value, string field, Dictionary<string, string[]> errors) => IsoDateQuery.Parse(value, field, errors);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PurchaseRequest(string? Description, DateOnly? PurchaseDate, decimal? TotalAmount, int? InstallmentCount, Guid? CategoryId)
{
    public CreditCardPurchaseInput ToInput() => new(Description, PurchaseDate, TotalAmount, InstallmentCount, CategoryId);
}

public sealed record InstallmentResponse(Guid Id, int Number, string Amount, CreditCardInstallmentStatus Status);
public sealed record PurchaseResponse(Guid Id, Guid CreditCardId, Guid CategoryId, string Description, DateOnly PurchaseDate, string TotalAmount, int InstallmentCount, CreditCardPurchaseStatus Status, IReadOnlyList<InstallmentResponse> Installments)
{
    public static PurchaseResponse From(FinancialCreditCardPurchaseDto p) => new(p.Id, p.CreditCardId, p.CategoryId, p.Description, p.PurchaseDate, p.TotalAmount, p.InstallmentCount, p.Status, p.Installments.Select(i => new InstallmentResponse(i.Id, i.Number, i.Amount, i.Status)).ToList());
}
