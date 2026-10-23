using System.Text.Json.Serialization;
using Saldo360.Api.Authentication;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.Common;

namespace Saldo360.Api.CreditCards;

public static class FinancialCreditCardEndpoints
{
    public static IEndpointRouteBuilder MapFinancialCreditCardEndpoints(this IEndpointRouteBuilder app)
    {
        var cards = app.MapGroup("/api/financial-workspaces/{workspaceId:guid}/credit-cards").RequireAuthorization();
        cards.MapGet("/", List);
        cards.MapGet("/summary", Summary);
        cards.MapGet("/{cardId:guid}", Get);
        cards.MapPost("/", Create).AddEndpointFilter<ValidateCsrfTokenFilter>();
        cards.MapPut("/{cardId:guid}", Update).AddEndpointFilter<ValidateCsrfTokenFilter>();
        cards.MapDelete("/{cardId:guid}", Archive).AddEndpointFilter<ValidateCsrfTokenFilter>();
        cards.MapPost("/{cardId:guid}/imports/email/latest", LatestEmail).AddEndpointFilter<ValidateCsrfTokenFilter>();
        return app;
    }

    private static async Task<IResult> LatestEmail(Guid workspaceId, Guid cardId, CurrentUser user, CardLatestEmailService service, CancellationToken ct)
    {
        if (await user.GetUserIdAsync(ct) is not { } userId) return Results.Unauthorized();
        var result = await service.SearchAsync(workspaceId, cardId, userId, ct);
        return result.Status == "NotFound" ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> Summary(Guid workspaceId, string? competence, CurrentUser user, FinancialCreditCardPurchaseService service, TimeProvider timeProvider, CancellationToken ct)
    {
        if (await user.GetUserIdAsync(ct) is not { } userId) return Results.Unauthorized();
        if (!DateOnly.TryParseExact(competence, "yyyy-MM", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["competence"] = ["Informe a competência no formato yyyy-MM."] });
        var result = await service.GetPortfolioAsync(workspaceId, userId, competence!, BusinessCalendar.Today(timeProvider.GetUtcNow()), ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> List(Guid workspaceId, bool? includeArchived, CurrentUser currentUser, FinancialCreditCardService cards, CancellationToken cancellationToken) =>
        await currentUser.GetUserIdAsync(cancellationToken) is not { } userId ? Results.Unauthorized() :
        await cards.ListAsync(workspaceId, userId, includeArchived ?? false, cancellationToken) is { } list ? Results.Ok(list.Select(CreditCardResponse.From)) : Results.NotFound();

    private static async Task<IResult> Get(Guid workspaceId, Guid cardId, CurrentUser currentUser, FinancialCreditCardService cards, CancellationToken cancellationToken) =>
        await currentUser.GetUserIdAsync(cancellationToken) is not { } userId ? Results.Unauthorized() :
        await cards.GetAsync(workspaceId, cardId, userId, cancellationToken) is { } card ? Results.Ok(CreditCardResponse.From(card)) : Results.NotFound();

    private static async Task<IResult> Create(Guid workspaceId, CreditCardRequest? request, CurrentUser currentUser, FinancialCreditCardService cards, CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        var result = await cards.CreateAsync(workspaceId, userId, request?.ToInput() ?? new(null, null, null, null, null, null, null, null), cancellationToken);
        return result.Access switch { CreditCardAccess.NotFound => Results.NotFound(), CreditCardAccess.Invalid => Results.ValidationProblem(result.Errors!), _ => Results.Created($"/api/financial-workspaces/{workspaceId}/credit-cards/{result.Card!.Id}", CreditCardResponse.From(result.Card)) };
    }

    private static async Task<IResult> Update(Guid workspaceId, Guid cardId, CreditCardRequest? request, CurrentUser currentUser, FinancialCreditCardService cards, CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        var result = await cards.UpdateAsync(workspaceId, cardId, userId, request?.ToInput() ?? new(null, null, null, null, null, null, null, null), cancellationToken);
        return result.Access switch { CreditCardAccess.NotFound => Results.NotFound(), CreditCardAccess.Invalid => Results.ValidationProblem(result.Errors!), _ => Results.Ok(CreditCardResponse.From(result.Card!)) };
    }

    private static async Task<IResult> Archive(Guid workspaceId, Guid cardId, CurrentUser currentUser, FinancialCreditCardService cards, CancellationToken cancellationToken) =>
        await currentUser.GetUserIdAsync(cancellationToken) is not { } userId ? Results.Unauthorized() :
        await cards.ArchiveAsync(workspaceId, cardId, userId, cancellationToken) ? Results.NoContent() : Results.NotFound();
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreditCardRequest(string? Name, string? Issuer, string? LastFourDigits, Guid? HolderUserId, decimal? CreditLimit, int? ClosingDay, int? DueDay, Guid? DefaultPaymentAccountId, string? GmailQuery = null)
{
    public CreditCardInput ToInput() => new(Name, Issuer, LastFourDigits, HolderUserId, CreditLimit, ClosingDay, DueDay, DefaultPaymentAccountId, GmailQuery);
}

public sealed record CreditCardResponse(Guid Id, string Name, string Issuer, string? LastFourDigits, Guid? HolderUserId, string CreditLimit, int ClosingDay, int DueDay, Guid? DefaultPaymentAccountId, bool IsArchived, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ArchivedAt, string? GmailQuery = null)
{
    public static CreditCardResponse From(FinancialCreditCardDto card) => new(card.Id, card.Name, card.Issuer, card.LastFourDigits, card.HolderUserId, card.CreditLimit, card.ClosingDay, card.DueDay, card.DefaultPaymentAccountId, card.IsArchived, card.CreatedAt, card.UpdatedAt, card.ArchivedAt, card.GmailQuery);
}
