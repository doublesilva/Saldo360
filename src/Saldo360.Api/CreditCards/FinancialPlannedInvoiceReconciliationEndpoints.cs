using System.Text.Json.Serialization;
using Saldo360.Api.Authentication;
using Saldo360.Application.CreditCards;
using Saldo360.Api.Observability;

namespace Saldo360.Api.CreditCards;

public static class FinancialPlannedInvoiceReconciliationEndpoints
{
    public static IEndpointRouteBuilder MapFinancialPlannedInvoiceReconciliationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/financial-workspaces/{workspaceId:guid}/invoice-reconciliations").RequireAuthorization();
        group.MapGet("/", List);
        group.MapPost("/", Create).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapDelete("/{id:guid}", Remove).AddEndpointFilter<ValidateCsrfTokenFilter>();
        return app;
    }

    private static async Task<IResult> List(Guid workspaceId, Guid? invoiceId, Guid? plannedTransactionId, CurrentUser user, FinancialPlannedInvoiceReconciliationService service, CancellationToken cancellationToken) =>
        await user.GetUserIdAsync(cancellationToken) is not { } userId
            ? Results.Unauthorized()
            : await service.ListAsync(workspaceId, userId, invoiceId, plannedTransactionId, cancellationToken) is { } list
                ? Results.Ok(list.Select(ReconciliationResponse.From))
                : Results.NotFound();

    private static async Task<IResult> Create(Guid workspaceId, ReconciliationRequest? request, CurrentUser user, FinancialPlannedInvoiceReconciliationService service, FinancialOperationLogger operations, HttpContext context, CancellationToken cancellationToken)
    {
        if (await user.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        var started = operations.Start();
        var result = await service.CreateAsync(workspaceId, userId,
            request is null ? new(null, null) : new(request.PlannedTransactionId, request.CreditCardInvoiceId), cancellationToken);
        var code = result.Access switch
        {
            FinancialPlannedInvoiceReconciliationAccess.Allowed => "NONE",
            FinancialPlannedInvoiceReconciliationAccess.Invalid => "VALIDATION_ERROR",
            FinancialPlannedInvoiceReconciliationAccess.Conflict => "OPERATION_CONFLICT",
            _ => "RESOURCE_NOT_FOUND",
        };
        operations.Completed(context, started, "InvoiceReconciliation", result.Access == FinancialPlannedInvoiceReconciliationAccess.Allowed ? "Succeeded" : "Rejected", code);
        return result.Access switch
        {
            FinancialPlannedInvoiceReconciliationAccess.NotFound => Results.NotFound(),
            FinancialPlannedInvoiceReconciliationAccess.Invalid => Results.ValidationProblem(result.Errors!),
            FinancialPlannedInvoiceReconciliationAccess.Conflict => Results.Conflict(new { message = result.Message }),
            _ => Results.Created($"/api/financial-workspaces/{workspaceId}/invoice-reconciliations/{result.Reconciliation!.Id}", ReconciliationResponse.From(result.Reconciliation)),
        };
    }

    private static async Task<IResult> Remove(Guid workspaceId, Guid id, CurrentUser user, FinancialPlannedInvoiceReconciliationService service, CancellationToken cancellationToken) =>
        await user.GetUserIdAsync(cancellationToken) is not { } userId ? Results.Unauthorized() :
        await service.RemoveAsync(workspaceId, id, userId, cancellationToken) ? Results.NoContent() : Results.NotFound();

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record ReconciliationRequest(Guid? PlannedTransactionId, Guid? CreditCardInvoiceId);
}

public sealed record ReconciliationResponse(Guid Id, Guid WorkspaceId, Guid PlannedTransactionId, Guid CreditCardInvoiceId, DateTimeOffset CreatedAt, DateTimeOffset? RemovedAt)
{
    public static ReconciliationResponse From(FinancialPlannedInvoiceReconciliationDto item) =>
        new(item.Id, item.WorkspaceId, item.PlannedTransactionId, item.CreditCardInvoiceId, item.CreatedAt, item.RemovedAt);
}
