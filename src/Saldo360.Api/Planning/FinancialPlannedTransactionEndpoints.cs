using System.Text.Json.Serialization;
using Saldo360.Api.Authentication;
using Saldo360.Api.Transactions;
using Saldo360.Application.Planning;
using Saldo360.Application.Transactions;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;

namespace Saldo360.Api.Planning;

public static class FinancialPlannedTransactionEndpoints
{
    public static IEndpointRouteBuilder MapFinancialPlannedTransactionEndpoints(this IEndpointRouteBuilder app)
    {
        var planned = app.MapGroup("/api/financial-workspaces/{workspaceId:guid}/planned-transactions").RequireAuthorization();
        planned.MapPost("/", Create).AddEndpointFilter<ValidateCsrfTokenFilter>();
        planned.MapGet("/", List);
        planned.MapGet("/{plannedId:guid}", Get);
        planned.MapPut("/{plannedId:guid}", Update).AddEndpointFilter<ValidateCsrfTokenFilter>();
        planned.MapPost("/{plannedId:guid}/cancel", Cancel).AddEndpointFilter<ValidateCsrfTokenFilter>();
        planned.MapPost("/{plannedId:guid}/realize", Realize).AddEndpointFilter<ValidateCsrfTokenFilter>();
        return app;
    }

    private static async Task<IResult> Create(
        Guid workspaceId, PlannedTransactionRequest? request, CurrentUser currentUser, FinancialPlannedTransactionService planned,
        CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        var result = await planned.CreateAsync(workspaceId, userId, ToInput(request), cancellationToken);
        return ToResponse(result, p => Results.Created(
            $"/api/financial-workspaces/{workspaceId}/planned-transactions/{p.Id}", PlannedTransactionResponse.From(p)));
    }

    /// <summary>
    /// Página de previsões (data prevista crescente, Id crescente). Filtros: from/to sobre a data prevista (yyyy-MM-dd,
    /// inclusivos), accountId, categoryId, type. Por padrão só pendentes; includeRealized=true e includeCancelled=true
    /// acrescentam realizadas e canceladas. pageSize de 1 a 100.
    /// </summary>
    private static async Task<IResult> List(
        Guid workspaceId, string? from, string? to, Guid? accountId, Guid? categoryId, string? type,
        bool? includeRealized, bool? includeCancelled, int? page, int? pageSize, CurrentUser currentUser,
        FinancialPlannedTransactionService planned, CancellationToken cancellationToken)
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

        var query = new PlannedTransactionQuery(
            fromDate, toDate, accountId, categoryId, type, includeRealized ?? false, includeCancelled ?? false, page, pageSize);
        var result = await planned.ListAsync(workspaceId, userId, query, cancellationToken);
        return result switch
        {
            { NotFound: true } => Results.NotFound(),
            { Errors: { } errors } => Results.ValidationProblem(errors),
            _ => Results.Ok(PlannedTransactionPageResponse.From(result.Page!)),
        };
    }

    private static async Task<IResult> Get(
        Guid workspaceId, Guid plannedId, CurrentUser currentUser, FinancialPlannedTransactionService planned,
        CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        return await planned.GetAsync(workspaceId, plannedId, userId, cancellationToken) is { } dto
            ? Results.Ok(PlannedTransactionResponse.From(dto))
            : Results.NotFound();
    }

    /// <summary>Substitui conta, categoria, tipo, descrição, valor e data prevista. Cancelada não pode ser editada (409).</summary>
    private static async Task<IResult> Update(
        Guid workspaceId, Guid plannedId, PlannedTransactionRequest? request, CurrentUser currentUser,
        FinancialPlannedTransactionService planned, CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        var result = await planned.UpdateAsync(workspaceId, plannedId, userId, ToInput(request), cancellationToken);
        return ToResponse(result, p => Results.Ok(PlannedTransactionResponse.From(p)));
    }

    /// <summary>Cancela sem excluir. Idempotente: cancelar de novo devolve 200 com o mesmo estado.</summary>
    private static async Task<IResult> Cancel(
        Guid workspaceId, Guid plannedId, CurrentUser currentUser, FinancialPlannedTransactionService planned,
        CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        var result = await planned.CancelAsync(workspaceId, plannedId, userId, cancellationToken);
        return ToResponse(result, p => Results.Ok(PlannedTransactionResponse.From(p)));
    }

    /// <summary>
    /// Realiza a previsão: cria o lançamento efetivo vinculado (201) com conta, categoria, tipo e descrição da previsão
    /// e o valor/data efetivos do corpo. Repetição com os mesmos dados devolve o existente (200); com outros, 409.
    /// Cancelada, 409. Conta/categoria inativas ou valor/data inválidos, 400.
    /// </summary>
    private static async Task<IResult> Realize(
        Guid workspaceId, Guid plannedId, RealizeRequest? request, CurrentUser currentUser,
        FinancialPlannedTransactionService planned, CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        var result = await planned.RealizeAsync(workspaceId, plannedId, userId, request?.Amount, request?.TransactionDate, cancellationToken);
        return result.Access switch
        {
            RealizeAccess.NotFound => Results.NotFound(),
            RealizeAccess.Invalid => Results.ValidationProblem(result.Errors!),
            RealizeAccess.Conflict => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: result.Message),
            RealizeAccess.Existing => Results.Ok(TransactionResponse.From(result.Transaction!)),
            _ => Results.Created(
                $"/api/financial-workspaces/{workspaceId}/transactions/{result.Transaction!.Id}", TransactionResponse.From(result.Transaction)),
        };
    }

    private static IResult ToResponse(PlannedTransactionResult result, Func<FinancialPlannedTransactionDto, IResult> ok) =>
        result.Access switch
        {
            PlannedTransactionAccess.NotFound => Results.NotFound(),
            PlannedTransactionAccess.Invalid => Results.ValidationProblem(result.Errors!),
            PlannedTransactionAccess.Conflict => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: result.Message),
            _ => ok(result.Planned!),
        };

    private static PlannedTransactionInput ToInput(PlannedTransactionRequest? request) => new(
        request?.AccountId, request?.CategoryId, request?.Type, request?.Description, request?.Amount, request?.DueDate);
}

/// <summary>
/// Criação e edição (substituição completa). <c>amount</c> positivo, até duas casas (texto "123.45", recomendado, ou
/// número); <c>dueDate</c> <c>yyyy-MM-dd</c>, passada, hoje ou futura. Família, autor, auditoria, moeda e cancelamento
/// são do servidor: enviá-los dá 400.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PlannedTransactionRequest(
    Guid? AccountId,
    Guid? CategoryId,
    string? Type,
    string? Description,
    decimal? Amount,
    DateOnly? DueDate);

/// <summary>Realização: só valor e data efetivos. Conta, categoria, tipo e descrição vêm da previsão.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RealizeRequest(decimal? Amount, DateOnly? TransactionDate);

/// <summary>
/// Previsão com valores como texto decimal exato, padrão Money. <c>amount</c>/<c>dueDate</c> são sempre os PREVISTOS;
/// <c>realization</c> traz o lançamento efetivo ativo (Id, valor e data efetivos) quando <c>status</c> é Realized.
/// Sem "vencida": é derivado na exibição.
/// </summary>
public sealed record PlannedTransactionResponse(
    Guid Id,
    CategoryType Type,
    string Amount,
    DateOnly DueDate,
    string Description,
    TransactionReferenceDto Account,
    TransactionReferenceDto Category,
    PlannedTransactionStatus Status,
    PlannedRealizationResponse? Realization,
    bool IsCancelled,
    string Currency,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? CancelledAt)
{
    public static PlannedTransactionResponse From(FinancialPlannedTransactionDto dto) => new(
        dto.Id,
        dto.Type,
        Money.Format(dto.Amount),
        dto.DueDate,
        dto.Description,
        dto.Account,
        dto.Category,
        dto.Status,
        dto.Realization is { } r ? new PlannedRealizationResponse(r.TransactionId, Money.Format(r.Amount), r.TransactionDate) : null,
        dto.IsCancelled,
        dto.Currency,
        dto.CreatedAt,
        dto.UpdatedAt,
        dto.CancelledAt);
}

public sealed record PlannedTransactionPageResponse(IReadOnlyList<PlannedTransactionResponse> Items, int Page, int PageSize, int TotalCount)
{
    public static PlannedTransactionPageResponse From(PlannedTransactionPage page) =>
        new(page.Items.Select(PlannedTransactionResponse.From).ToList(), page.Page, page.PageSize, page.TotalCount);
}

/// <summary>Lançamento efetivo ativo de uma previsão realizada: valor efetivo como texto exato.</summary>
public sealed record PlannedRealizationResponse(Guid TransactionId, string Amount, DateOnly TransactionDate);
