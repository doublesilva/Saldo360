using System.Text.Json.Serialization;
using Saldo360.Api.Authentication;
using Saldo360.Application.CreditCards;

namespace Saldo360.Api.CreditCards;

public static class FinancialPlannedInvoiceReconciliationEndpoints
{
    public static IEndpointRouteBuilder MapFinancialPlannedInvoiceReconciliationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/families/{familyId:guid}/invoice-reconciliations").RequireAuthorization();
        group.MapGet("/", List);
        group.MapPost("/", Create).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapDelete("/{id:guid}", Remove).AddEndpointFilter<ValidateCsrfTokenFilter>();
        return app;
    }

    private static async Task<IResult> List(Guid familyId, Guid? invoiceId, Guid? plannedTransactionId, CurrentUser user, FinancialPlannedInvoiceReconciliationService service, CancellationToken cancellationToken) =>
        await user.GetUserIdAsync(cancellationToken) is not { } userId
            ? Results.Unauthorized()
            : await service.ListAsync(familyId, userId, invoiceId, plannedTransactionId, cancellationToken) is { } list
                ? Results.Ok(list.Select(ReconciliationResponse.From))
                : Results.NotFound();

    private static async Task<IResult> Create(Guid familyId, ReconciliationRequest? request, CurrentUser user, FinancialPlannedInvoiceReconciliationService service, CancellationToken cancellationToken)
    {
        if (await user.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        var result = await service.CreateAsync(familyId, userId,
            request is null ? new(null, null) : new(request.PlannedTransactionId, request.CreditCardInvoiceId), cancellationToken);
        return result.Access switch
        {
            FinancialPlannedInvoiceReconciliationAccess.NotFound => Results.NotFound(),
            FinancialPlannedInvoiceReconciliationAccess.Invalid => Results.ValidationProblem(result.Errors!),
            FinancialPlannedInvoiceReconciliationAccess.Conflict => Results.Conflict(new { message = result.Message }),
            _ => Results.Created($"/api/families/{familyId}/invoice-reconciliations/{result.Reconciliation!.Id}", ReconciliationResponse.From(result.Reconciliation)),
        };
    }

    private static async Task<IResult> Remove(Guid familyId, Guid id, CurrentUser user, FinancialPlannedInvoiceReconciliationService service, CancellationToken cancellationToken) =>
        await user.GetUserIdAsync(cancellationToken) is not { } userId ? Results.Unauthorized() :
        await service.RemoveAsync(familyId, id, userId, cancellationToken) ? Results.NoContent() : Results.NotFound();

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record ReconciliationRequest(Guid? PlannedTransactionId, Guid? CreditCardInvoiceId);
}

public sealed record ReconciliationResponse(Guid Id, Guid FamilyId, Guid PlannedTransactionId, Guid CreditCardInvoiceId, DateTimeOffset CreatedAt, DateTimeOffset? RemovedAt)
{
    public static ReconciliationResponse From(FinancialPlannedInvoiceReconciliationDto item) =>
        new(item.Id, item.FamilyId, item.PlannedTransactionId, item.CreditCardInvoiceId, item.CreatedAt, item.RemovedAt);
}
