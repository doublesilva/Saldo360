using Saldo360.Api.Authentication;
using Saldo360.Application.Transfers;

namespace Saldo360.Api.Transfers;

/// <summary>Transferências previstas: criar, listar por período, cancelar e associar/desassociar a uma transferência realizada. Nunca criam transferência nem lançamento.</summary>
public static class FinancialPlannedTransferEndpoints
{
    public static IEndpointRouteBuilder MapFinancialPlannedTransferEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/financial-workspaces/{workspaceId:guid}/planned-transfers").RequireAuthorization();
        group.MapPost("/", Create).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapGet("/", List);
        group.MapPost("/{id:guid}/cancel", Cancel).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapPost("/{id:guid}/link", Link).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapPost("/{id:guid}/unlink", Unlink).AddEndpointFilter<ValidateCsrfTokenFilter>();
        return app;
    }

    private static async Task<IResult> Create(Guid workspaceId, PlannedTransferRequest? request, CurrentUser currentUser, FinancialPlannedTransferService service, CancellationToken ct)
    {
        if (await currentUser.GetUserIdAsync(ct) is not { } userId) return Results.Unauthorized();
        var result = await service.CreateAsync(workspaceId, userId, new(request?.SourceAccountId, request?.DestinationAccountId, request?.Amount, request?.DueDate, request?.Description), ct);
        return Respond(result, value => Results.Created($"/api/financial-workspaces/{workspaceId}/planned-transfers/{value.Id}", PlannedTransferResponse.From(value)));
    }

    /// <summary>from/through (yyyy-MM-dd, obrigatórios) pela data prevista; canceladas só com includeCancelled=true.</summary>
    private static async Task<IResult> List(Guid workspaceId, string? from, string? through, bool? includeCancelled, CurrentUser currentUser, FinancialPlannedTransferService service, CancellationToken ct)
    {
        if (await currentUser.GetUserIdAsync(ct) is not { } userId) return Results.Unauthorized();
        var errors = new Dictionary<string, string[]>();
        var fromDate = IsoDateQuery.Parse(from, "from", errors);
        var throughDate = IsoDateQuery.Parse(through, "through", errors);
        if (fromDate is null && !errors.ContainsKey("from")) errors["from"] = ["Informe a data inicial."];
        if (throughDate is null && !errors.ContainsKey("through")) errors["through"] = ["Informe a data final."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);
        if (throughDate < fromDate) return Results.ValidationProblem(new Dictionary<string, string[]> { ["through"] = ["A data final deve ser posterior ou igual à inicial."] });
        return await service.ListAsync(workspaceId, userId, fromDate!.Value, throughDate!.Value, includeCancelled ?? false, ct) is { } list
            ? Results.Ok(list.Select(PlannedTransferResponse.From).ToList()) : Results.NotFound();
    }

    private static async Task<IResult> Cancel(Guid workspaceId, Guid id, CurrentUser currentUser, FinancialPlannedTransferService service, CancellationToken ct) =>
        await currentUser.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized() : Respond(await service.CancelAsync(workspaceId, userId, id, ct), value => Results.Ok(PlannedTransferResponse.From(value)));

    private static async Task<IResult> Link(Guid workspaceId, Guid id, PlannedTransferLinkRequest? request, CurrentUser currentUser, FinancialPlannedTransferService service, CancellationToken ct) =>
        await currentUser.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized() : Respond(await service.LinkAsync(workspaceId, userId, id, request?.TransferId, ct), value => Results.Ok(PlannedTransferResponse.From(value)));

    private static async Task<IResult> Unlink(Guid workspaceId, Guid id, CurrentUser currentUser, FinancialPlannedTransferService service, CancellationToken ct) =>
        await currentUser.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized() : Respond(await service.UnlinkAsync(workspaceId, userId, id, ct), value => Results.Ok(PlannedTransferResponse.From(value)));

    private static IResult Respond(PlannedTransferResult result, Func<PlannedTransferDto, IResult> ok) => result.Outcome switch
    {
        PlannedTransferOutcome.Ok => ok(result.Value!),
        PlannedTransferOutcome.NotFound => Results.NotFound(),
        PlannedTransferOutcome.Invalid => Results.ValidationProblem(result.Errors!),
        _ => Results.Conflict(new { message = result.Message }),
    };
}

public sealed record PlannedTransferRequest(Guid? SourceAccountId, Guid? DestinationAccountId, decimal? Amount, DateOnly? DueDate, string? Description);

public sealed record PlannedTransferLinkRequest(Guid? TransferId);

public sealed record PlannedTransferResponse(
    Guid Id, TransferAccountDto SourceAccount, TransferAccountDto DestinationAccount, string Amount, DateOnly DueDate, string Description, string Status, Guid? RealizedTransferId,
    string? LinkOrigin, DateTimeOffset CreatedAt, DateTimeOffset? CancelledAt, IReadOnlyList<PlannedTransferCandidateResponse> Candidates)
{
    public static PlannedTransferResponse From(PlannedTransferDto dto) => new(dto.Id, dto.SourceAccount, dto.DestinationAccount, Saldo360.Domain.Common.Money.Format(dto.Amount), dto.DueDate, dto.Description, dto.Status,
        dto.RealizedTransferId, dto.LinkOrigin, dto.CreatedAt, dto.CancelledAt, dto.Candidates.Select(c => new PlannedTransferCandidateResponse(c.TransferId, c.TransferDate, Saldo360.Domain.Common.Money.Format(c.Amount), c.Description)).ToList());
}

public sealed record PlannedTransferCandidateResponse(Guid TransferId, DateOnly TransferDate, string Amount, string Description);
