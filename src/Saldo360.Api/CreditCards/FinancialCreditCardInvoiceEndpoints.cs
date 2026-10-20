using System.Globalization;
using System.Text.Json.Serialization;
using Saldo360.Api.Authentication;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.Common;
using Saldo360.Api.Observability;

namespace Saldo360.Api.CreditCards;

public static class FinancialCreditCardInvoiceEndpoints
{
    public static IEndpointRouteBuilder MapFinancialCreditCardInvoiceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/financial-workspaces/{workspaceId:guid}/credit-cards/{cardId:guid}/invoices").RequireAuthorization();
        group.MapGet("/", List);
        group.MapGet("/{invoiceId:guid}", Get);
        group.MapGet("/{invoiceId:guid}/payments", ListPayments);
        group.MapPost("/{invoiceId:guid}/payments", CreatePayment).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapPost("/{invoiceId:guid}/payments/{paymentId:guid}/cancel", CancelPayment).AddEndpointFilter<ValidateCsrfTokenFilter>();
        app.MapGet("/api/financial-workspaces/{workspaceId:guid}/credit-cards/{cardId:guid}/invoice-projection", Projection).RequireAuthorization();
        app.MapGet("/api/financial-workspaces/{workspaceId:guid}/credit-cards/{cardId:guid}/limit", Limit).RequireAuthorization();
        return app;
    }

    private static async Task<IResult> List(Guid workspaceId, Guid cardId, string? from, string? through, CurrentUser user, FinancialCreditCardPurchaseService service, TimeProvider timeProvider, CancellationToken ct)
    {
        if (await user.GetUserIdAsync(ct) is not { } userId) return Results.Unauthorized();
        var errors = new Dictionary<string, string[]>();
        var start = Parse(from, "from", errors); var end = Parse(through, "through", errors);
        if (start is not null && end is not null && end < start) errors["through"] = ["through deve ser maior ou igual a from."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);
        var result = await service.ListInvoicesAsync(workspaceId, cardId, userId, start!.Value.ToString("yyyy-MM"), end!.Value.ToString("yyyy-MM"), BusinessCalendar.Today(timeProvider.GetUtcNow()), ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> Get(Guid workspaceId, Guid cardId, Guid invoiceId, CurrentUser user, FinancialCreditCardPurchaseService service, TimeProvider timeProvider, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized() : await service.GetInvoiceAsync(workspaceId, cardId, invoiceId, userId, BusinessCalendar.Today(timeProvider.GetUtcNow()), ct) is { } invoice ? Results.Ok(invoice) : Results.NotFound();

    private static async Task<IResult> Projection(Guid workspaceId, Guid cardId, string? from, int? months, CurrentUser user, FinancialCreditCardPurchaseService service, TimeProvider timeProvider, CancellationToken ct)
    {
        if (await user.GetUserIdAsync(ct) is not { } userId) return Results.Unauthorized();
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(from) || !DateOnly.TryParseExact(from, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) errors["from"] = ["from deve seguir o formato yyyy-MM."];
        if (months is < 1 or > 12) errors["months"] = ["months deve estar entre 1 e 12."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);
        var result = await service.ProjectInvoicesAsync(workspaceId, cardId, userId, from!, months!.Value, BusinessCalendar.Today(timeProvider.GetUtcNow()), ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> Limit(Guid workspaceId, Guid cardId, CurrentUser user, FinancialCreditCardPurchaseService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized() : await service.GetLimitAsync(workspaceId, cardId, userId, ct) is { } limit ? Results.Ok(limit) : Results.NotFound();

    private static async Task<IResult> ListPayments(Guid workspaceId, Guid cardId, Guid invoiceId, CurrentUser user, FinancialCreditCardInvoicePaymentService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized() : await service.ListAsync(workspaceId, cardId, invoiceId, userId, ct) is { } payments ? Results.Ok(payments) : Results.NotFound();

    private static async Task<IResult> CreatePayment(Guid workspaceId, Guid cardId, Guid invoiceId, PaymentRequest? request, CurrentUser user, FinancialCreditCardInvoicePaymentService service, FinancialOperationLogger operations, HttpContext context, CancellationToken ct)
    {
        if (await user.GetUserIdAsync(ct) is not { } userId) return Results.Unauthorized();
        var started = operations.Start();
        var result = await service.CreateAsync(workspaceId, cardId, invoiceId, userId, request is null ? new(null, null, null, null) : new(request.OperationId, request.FinancialAccountId, request.PaymentDate, request.Amount), ct);
        var code = result.Access switch
        {
            FinancialCreditCardInvoicePaymentAccess.Allowed => "NONE",
            FinancialCreditCardInvoicePaymentAccess.Invalid => "VALIDATION_ERROR",
            FinancialCreditCardInvoicePaymentAccess.Conflict => "OPERATION_CONFLICT",
            _ => "RESOURCE_NOT_FOUND",
        };
        operations.Completed(context, started, "CreditCardInvoicePayment", result.Access == FinancialCreditCardInvoicePaymentAccess.Allowed ? "Succeeded" : "Rejected", code);
        return result.Access switch { FinancialCreditCardInvoicePaymentAccess.NotFound => Results.NotFound(), FinancialCreditCardInvoicePaymentAccess.Invalid => Results.ValidationProblem(result.Errors!), FinancialCreditCardInvoicePaymentAccess.Conflict => Results.Conflict(new { message = result.Message }), _ => Results.Created($"/api/financial-workspaces/{workspaceId}/credit-cards/{cardId}/invoices/{invoiceId}/payments/{result.Payment!.Id}", result.Payment) };
    }

    private static async Task<IResult> CancelPayment(Guid workspaceId, Guid cardId, Guid invoiceId, Guid paymentId, CurrentUser user, FinancialCreditCardInvoicePaymentService service, CancellationToken ct)
    {
        if (await user.GetUserIdAsync(ct) is not { } userId) return Results.Unauthorized();
        var result = await service.CancelAsync(workspaceId, cardId, invoiceId, paymentId, userId, ct);
        return result.Access == FinancialCreditCardInvoicePaymentAccess.NotFound ? Results.NotFound() : Results.Ok(result.Payment);
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record PaymentRequest(Guid? OperationId, Guid? FinancialAccountId, DateOnly? PaymentDate, decimal? Amount);

    private static DateOnly? Parse(string? value, string field, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(value) || !DateOnly.TryParseExact(value, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
        { errors[field] = [$"{field} deve seguir o formato yyyy-MM."]; return null; }
        return new DateOnly(month.Year, month.Month, 1);
    }
}
