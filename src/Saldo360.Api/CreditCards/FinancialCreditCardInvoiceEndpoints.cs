using System.Globalization;
using Saldo360.Api.Authentication;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.Common;

namespace Saldo360.Api.CreditCards;

public static class FinancialCreditCardInvoiceEndpoints
{
    public static IEndpointRouteBuilder MapFinancialCreditCardInvoiceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/families/{familyId:guid}/credit-cards/{cardId:guid}/invoices").RequireAuthorization();
        group.MapGet("/", List);
        group.MapGet("/{invoiceId:guid}", Get);
        return app;
    }

    private static async Task<IResult> List(Guid familyId, Guid cardId, string? from, string? through, CurrentUser user, FinancialCreditCardPurchaseService service, TimeProvider timeProvider, CancellationToken ct)
    {
        if (await user.GetUserIdAsync(ct) is not { } userId) return Results.Unauthorized();
        var errors = new Dictionary<string, string[]>();
        var start = Parse(from, "from", errors); var end = Parse(through, "through", errors);
        if (start is not null && end is not null && end < start) errors["through"] = ["through deve ser maior ou igual a from."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);
        var result = await service.ListInvoicesAsync(familyId, cardId, userId, start!.Value.ToString("yyyy-MM"), end!.Value.ToString("yyyy-MM"), BusinessCalendar.Today(timeProvider.GetUtcNow()), ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> Get(Guid familyId, Guid cardId, Guid invoiceId, CurrentUser user, FinancialCreditCardPurchaseService service, TimeProvider timeProvider, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized() : await service.GetInvoiceAsync(familyId, cardId, invoiceId, userId, BusinessCalendar.Today(timeProvider.GetUtcNow()), ct) is { } invoice ? Results.Ok(invoice) : Results.NotFound();

    private static DateOnly? Parse(string? value, string field, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || !DateOnly.TryParseExact(value, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
        { errors[field] = [$"{field} deve seguir o formato yyyy-MM."]; return null; }
        return new DateOnly(month.Year, month.Month, 1);
    }
}
