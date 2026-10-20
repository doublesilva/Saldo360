using System.Text.Json.Serialization;
using Saldo360.Api.Authentication;
using Saldo360.Application.Transfers;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Common;

namespace Saldo360.Api.Transfers;

public static class FinancialTransferEndpoints
{
    public static IEndpointRouteBuilder MapFinancialTransferEndpoints(this IEndpointRouteBuilder app)
    {
        var transfers = app.MapGroup("/api/financial-workspaces/{workspaceId:guid}/transfers").RequireAuthorization();
        transfers.MapPost("/", Create).AddEndpointFilter<ValidateCsrfTokenFilter>();
        transfers.MapGet("/", List);
        transfers.MapGet("/{transferId:guid}", Get);
        transfers.MapPost("/{transferId:guid}/cancel", Cancel).AddEndpointFilter<ValidateCsrfTokenFilter>();
        return app;
    }

    private static async Task<IResult> Create(
        Guid workspaceId, TransferRequest? request, CurrentUser currentUser, FinancialTransferService transfers,
        CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        var input = new TransferInput(
            request?.SourceAccountId, request?.DestinationAccountId, request?.Amount, request?.TransferDate, request?.Description);
        var result = await transfers.CreateAsync(workspaceId, userId, input, cancellationToken);
        return ToResponse(result, t => Results.Created($"/api/financial-workspaces/{workspaceId}/transfers/{t.Id}", TransferResponse.From(t)));
    }

    /// <summary>
    /// Página de transferências (data decrescente, Id decrescente). Filtros: from/to (yyyy-MM-dd, inclusivos) e
    /// accountId (origem OU destino). Canceladas só com includeCancelled=true. pageSize de 1 a 100.
    /// </summary>
    private static async Task<IResult> List(
        Guid workspaceId, string? from, string? to, Guid? accountId, bool? includeCancelled, int? page, int? pageSize,
        CurrentUser currentUser, FinancialTransferService transfers, CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        var dateErrors = new Dictionary<string, string[]>();
        var fromDate = IsoDateQuery.Parse(from, "from", dateErrors);
        var toDate = IsoDateQuery.Parse(to, "to", dateErrors);
        if (dateErrors.Count > 0)
        {
            return Results.ValidationProblem(dateErrors);
        }

        var query = new TransferQuery(fromDate, toDate, accountId, includeCancelled ?? false, page, pageSize);
        var result = await transfers.ListAsync(workspaceId, userId, query, cancellationToken);
        return result switch
        {
            { NotFound: true } => Results.NotFound(),
            { Errors: { } errors } => Results.ValidationProblem(errors),
            _ => Results.Ok(new TransferPageResponse(
                result.Page!.Items.Select(TransferResponse.From).ToList(), result.Page.Page, result.Page.PageSize, result.Page.TotalCount)),
        };
    }

    private static async Task<IResult> Get(
        Guid workspaceId, Guid transferId, CurrentUser currentUser, FinancialTransferService transfers,
        CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        return await transfers.GetAsync(workspaceId, transferId, userId, cancellationToken) is { } transfer
            ? Results.Ok(TransferResponse.From(transfer))
            : Results.NotFound();
    }

    /// <summary>Cancela sem excluir. Idempotente: cancelar de novo devolve 200 com o mesmo estado.</summary>
    private static async Task<IResult> Cancel(
        Guid workspaceId, Guid transferId, CurrentUser currentUser, FinancialTransferService transfers,
        CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        var result = await transfers.CancelAsync(workspaceId, transferId, userId, cancellationToken);
        return ToResponse(result, t => Results.Ok(TransferResponse.From(t)));
    }

    private static IResult ToResponse(TransferResult result, Func<FinancialTransferDto, IResult> ok) => result.Access switch
    {
        TransferAccess.NotFound => Results.NotFound(),
        TransferAccess.Invalid => Results.ValidationProblem(result.Errors!),
        _ => ok(result.Transfer!),
    };
}

/// <summary>
/// Criação. <c>amount</c> positivo, até duas casas (número JSON ou texto "500.00"); <c>transferDate</c>
/// <c>yyyy-MM-dd</c>, não futura em America/Sao_Paulo. Sem categoria. Família, autor, moeda e cancelamento são
/// do servidor: enviá-los dá 400.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TransferRequest(
    Guid? SourceAccountId,
    Guid? DestinationAccountId,
    decimal? Amount,
    DateOnly? TransferDate,
    string? Description);

/// <summary>Transferência com o valor como texto decimal exato ("500.00"), padrão Money.</summary>
public sealed record TransferResponse(
    Guid Id,
    string Amount,
    DateOnly TransferDate,
    string Description,
    TransferAccountResponse SourceAccount,
    TransferAccountResponse DestinationAccount,
    bool IsCancelled,
    string Currency,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CancelledAt)
{
    public static TransferResponse From(FinancialTransferDto dto) => new(
        dto.Id,
        Money.Format(dto.Amount),
        dto.TransferDate,
        dto.Description,
        TransferAccountResponse.From(dto.SourceAccount),
        TransferAccountResponse.From(dto.DestinationAccount),
        dto.IsCancelled,
        dto.Currency,
        dto.CreatedAt,
        dto.CancelledAt);
}

public sealed record TransferAccountResponse(Guid Id, string Name, FinancialScope FinancialScope, bool IsActive)
{
    public static TransferAccountResponse From(TransferAccountDto dto) => new(dto.Id, dto.Name, dto.FinancialScope, dto.IsActive);
}

public sealed record TransferPageResponse(IReadOnlyList<TransferResponse> Items, int Page, int PageSize, int TotalCount);
