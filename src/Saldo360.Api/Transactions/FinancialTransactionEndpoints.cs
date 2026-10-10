using System.Text.Json.Serialization;
using Saldo360.Api.Authentication;
using Saldo360.Application.Transactions;

namespace Saldo360.Api.Transactions;

public static class FinancialTransactionEndpoints
{
    public static IEndpointRouteBuilder MapFinancialTransactionEndpoints(this IEndpointRouteBuilder app)
    {
        var transactions = app.MapGroup("/api/families/{familyId:guid}/transactions").RequireAuthorization();
        transactions.MapPost("/", Create).AddEndpointFilter<ValidateCsrfTokenFilter>();
        transactions.MapGet("/", List);
        transactions.MapGet("/{transactionId:guid}", Get);
        transactions.MapPut("/{transactionId:guid}", Update).AddEndpointFilter<ValidateCsrfTokenFilter>();
        transactions.MapPost("/{transactionId:guid}/cancel", Cancel).AddEndpointFilter<ValidateCsrfTokenFilter>();
        return app;
    }

    private static async Task<IResult> Create(
        Guid familyId, TransactionRequest? request, CurrentUser currentUser, FinancialTransactionService transactions,
        CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        var result = await transactions.CreateAsync(familyId, userId, ToInput(request), cancellationToken);
        return ToResponse(result, t => Results.Created($"/api/families/{familyId}/transactions/{t.Id}", t));
    }

    /// <summary>
    /// Página de lançamentos (data decrescente, Id decrescente). Filtros opcionais: from/to (yyyy-MM-dd, inclusivos),
    /// accountId, categoryId, type (Income/Expense). Cancelados só com includeCancelled=true. pageSize até 100.
    /// </summary>
    private static async Task<IResult> List(
        Guid familyId, string? from, string? to, Guid? accountId, Guid? categoryId, string? type,
        bool? includeCancelled, int? page, int? pageSize, CurrentUser currentUser,
        FinancialTransactionService transactions, CancellationToken cancellationToken)
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

        var query = new TransactionQuery(fromDate, toDate, accountId, categoryId, type, includeCancelled ?? false, page, pageSize);
        var result = await transactions.ListAsync(familyId, userId, query, cancellationToken);
        return result switch
        {
            { NotFound: true } => Results.NotFound(),
            { Errors: { } errors } => Results.ValidationProblem(errors),
            _ => Results.Ok(result.Page),
        };
    }

    private static async Task<IResult> Get(
        Guid familyId, Guid transactionId, CurrentUser currentUser, FinancialTransactionService transactions,
        CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        return await transactions.GetAsync(familyId, transactionId, userId, cancellationToken) is { } transaction
            ? Results.Ok(transaction)
            : Results.NotFound();
    }

    /// <summary>Substitui conta, categoria, tipo, descrição, valor e data. Cancelado não pode ser editado (409).</summary>
    private static async Task<IResult> Update(
        Guid familyId, Guid transactionId, TransactionRequest? request, CurrentUser currentUser,
        FinancialTransactionService transactions, CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        var result = await transactions.UpdateAsync(familyId, transactionId, userId, ToInput(request), cancellationToken);
        return ToResponse(result, Results.Ok);
    }

    /// <summary>Cancela sem excluir. Idempotente: cancelar de novo devolve 200 com o mesmo estado.</summary>
    private static async Task<IResult> Cancel(
        Guid familyId, Guid transactionId, CurrentUser currentUser, FinancialTransactionService transactions,
        CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        return ToResponse(await transactions.CancelAsync(familyId, transactionId, userId, cancellationToken), Results.Ok);
    }

    private static IResult ToResponse(TransactionResult result, Func<FinancialTransactionDto, IResult> ok) => result.Access switch
    {
        TransactionAccess.NotFound => Results.NotFound(),
        TransactionAccess.Invalid => Results.ValidationProblem(result.Errors!),
        TransactionAccess.Cancelled => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: TransactionResult.CancelledMessage),
        _ => ok(result.Transaction!),
    };

    private static TransactionInput ToInput(TransactionRequest? request) => new(
        request?.AccountId, request?.CategoryId, request?.Type, request?.Description, request?.Amount, request?.TransactionDate);
}

/// <summary>
/// Criação e edição (substituição completa). <c>amount</c> é positivo, até duas casas (número JSON ou texto
/// "123.45"); <c>transactionDate</c> é <c>yyyy-MM-dd</c>, sem horário, e não pode ser futura em America/Sao_Paulo.
/// Família, autor, datas de auditoria, moeda e cancelamento são do servidor: enviá-los dá 400.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TransactionRequest(
    Guid? AccountId,
    Guid? CategoryId,
    string? Type,
    string? Description,
    decimal? Amount,
    DateOnly? TransactionDate);
