using System.Text.Json.Serialization;
using Saldo360.Api.Authentication;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.Common;

namespace Saldo360.Api.CreditCards;

public static class FinancialCreditCardEndpoints
{
    public static IEndpointRouteBuilder MapFinancialCreditCardEndpoints(this IEndpointRouteBuilder app)
    {
        var cards = app.MapGroup("/api/families/{familyId:guid}/credit-cards").RequireAuthorization();
        cards.MapGet("/", List);
        cards.MapGet("/{cardId:guid}", Get);
        cards.MapPost("/", Create).AddEndpointFilter<ValidateCsrfTokenFilter>();
        cards.MapPut("/{cardId:guid}", Update).AddEndpointFilter<ValidateCsrfTokenFilter>();
        cards.MapDelete("/{cardId:guid}", Archive).AddEndpointFilter<ValidateCsrfTokenFilter>();
        return app;
    }

    private static async Task<IResult> List(Guid familyId, bool? includeArchived, CurrentUser currentUser, FinancialCreditCardService cards, CancellationToken cancellationToken) =>
        await currentUser.GetUserIdAsync(cancellationToken) is not { } userId ? Results.Unauthorized() :
        await cards.ListAsync(familyId, userId, includeArchived ?? false, cancellationToken) is { } list ? Results.Ok(list.Select(CreditCardResponse.From)) : Results.NotFound();

    private static async Task<IResult> Get(Guid familyId, Guid cardId, CurrentUser currentUser, FinancialCreditCardService cards, CancellationToken cancellationToken) =>
        await currentUser.GetUserIdAsync(cancellationToken) is not { } userId ? Results.Unauthorized() :
        await cards.GetAsync(familyId, cardId, userId, cancellationToken) is { } card ? Results.Ok(CreditCardResponse.From(card)) : Results.NotFound();

    private static async Task<IResult> Create(Guid familyId, CreditCardRequest? request, CurrentUser currentUser, FinancialCreditCardService cards, CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        var result = await cards.CreateAsync(familyId, userId, request?.ToInput() ?? new(null, null, null, null, null, null, null), cancellationToken);
        return result.Access switch { CreditCardAccess.NotFound => Results.NotFound(), CreditCardAccess.Invalid => Results.ValidationProblem(result.Errors!), _ => Results.Created($"/api/families/{familyId}/credit-cards/{result.Card!.Id}", CreditCardResponse.From(result.Card)) };
    }

    private static async Task<IResult> Update(Guid familyId, Guid cardId, CreditCardRequest? request, CurrentUser currentUser, FinancialCreditCardService cards, CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        var result = await cards.UpdateAsync(familyId, cardId, userId, request?.ToInput() ?? new(null, null, null, null, null, null, null), cancellationToken);
        return result.Access switch { CreditCardAccess.NotFound => Results.NotFound(), CreditCardAccess.Invalid => Results.ValidationProblem(result.Errors!), _ => Results.Ok(CreditCardResponse.From(result.Card!)) };
    }

    private static async Task<IResult> Archive(Guid familyId, Guid cardId, CurrentUser currentUser, FinancialCreditCardService cards, CancellationToken cancellationToken) =>
        await currentUser.GetUserIdAsync(cancellationToken) is not { } userId ? Results.Unauthorized() :
        await cards.ArchiveAsync(familyId, cardId, userId, cancellationToken) ? Results.NoContent() : Results.NotFound();
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreditCardRequest(string? Name, string? Issuer, Guid? HolderUserId, decimal? CreditLimit, int? ClosingDay, int? DueDay, Guid? DefaultPaymentAccountId)
{
    public CreditCardInput ToInput() => new(Name, Issuer, HolderUserId, CreditLimit, ClosingDay, DueDay, DefaultPaymentAccountId);
}

public sealed record CreditCardResponse(Guid Id, string Name, string Issuer, Guid? HolderUserId, string CreditLimit, int ClosingDay, int DueDay, Guid? DefaultPaymentAccountId, bool IsArchived, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ArchivedAt)
{
    public static CreditCardResponse From(FinancialCreditCardDto card) => new(card.Id, card.Name, card.Issuer, card.HolderUserId, card.CreditLimit, card.ClosingDay, card.DueDay, card.DefaultPaymentAccountId, card.IsArchived, card.CreatedAt, card.UpdatedAt, card.ArchivedAt);
}
