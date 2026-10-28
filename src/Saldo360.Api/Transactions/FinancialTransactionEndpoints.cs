using System.Text.Json.Serialization;
using Saldo360.Api.Authentication;
using Saldo360.Application.Transactions;
using Saldo360.Domain.Transactions;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;

namespace Saldo360.Api.Transactions;

public static class FinancialTransactionEndpoints
{
    public static IEndpointRouteBuilder MapFinancialTransactionEndpoints(this IEndpointRouteBuilder app)
    {
        var transactions = app.MapGroup("/api/financial-workspaces/{workspaceId:guid}/transactions").RequireAuthorization();
        transactions.MapPost("/", Create).AddEndpointFilter<ValidateCsrfTokenFilter>();
        transactions.MapGet("/", List);
        transactions.MapGet("/{transactionId:guid}", Get);
        transactions.MapPut("/{transactionId:guid}", Update).AddEndpointFilter<ValidateCsrfTokenFilter>();
        transactions.MapPut("/{transactionId:guid}/category", Recategorize).AddEndpointFilter<ValidateCsrfTokenFilter>();
        transactions.MapPost("/{transactionId:guid}/cancel", Cancel).AddEndpointFilter<ValidateCsrfTokenFilter>();
        return app;
    }

    private static async Task<IResult> Create(
        Guid workspaceId, TransactionRequest? request, CurrentUser currentUser, FinancialTransactionService transactions,
        CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        var result = await transactions.CreateAsync(workspaceId, userId, ToInput(request), cancellationToken);
        return ToResponse(result, t => Results.Created($"/api/financial-workspaces/{workspaceId}/transactions/{t.Id}", TransactionResponse.From(t)));
    }

    /// <summary>
    /// Página de lançamentos (data decrescente, Id decrescente). Filtros opcionais: from/to (yyyy-MM-dd, inclusivos),
    /// accountId, categoryId, type (Income/Expense). Cancelados só com includeCancelled=true. pageSize até 100.
    /// </summary>
    private static async Task<IResult> List(
        Guid workspaceId, string? from, string? to, Guid? accountId, Guid? categoryId, string? type,
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
        var result = await transactions.ListAsync(workspaceId, userId, query, cancellationToken);
        return result switch
        {
            { NotFound: true } => Results.NotFound(),
            { Errors: { } errors } => Results.ValidationProblem(errors),
            _ => Results.Ok(TransactionPageResponse.From(result.Page!)),
        };
    }

    private static async Task<IResult> Get(
        Guid workspaceId, Guid transactionId, CurrentUser currentUser, FinancialTransactionService transactions,
        CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        return await transactions.GetAsync(workspaceId, transactionId, userId, cancellationToken) is { } transaction
            ? Results.Ok(TransactionResponse.From(transaction))
            : Results.NotFound();
    }

    /// <summary>Substitui conta, categoria, tipo, descrição, valor e data. Cancelado não pode ser editado (409).</summary>
    private static async Task<IResult> Update(
        Guid workspaceId, Guid transactionId, TransactionRequest? request, CurrentUser currentUser,
        FinancialTransactionService transactions, CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        var result = await transactions.UpdateAsync(workspaceId, transactionId, userId, ToInput(request), cancellationToken);
        return ToResponse(result, t => Results.Ok(TransactionResponse.From(t)));
    }

    /// <summary>Corrige só a categoria de um lançamento importado da Pluggy (T09.3); os dados de origem ficam intactos. Categoria nula a remove.</summary>
    private static async Task<IResult> Recategorize(
        Guid workspaceId, Guid transactionId, CategoryRequest? request, CurrentUser currentUser, FinancialTransactionService transactions,
        CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        return ToResponse(
            await transactions.RecategorizeImportedAsync(workspaceId, transactionId, userId, request?.CategoryId, cancellationToken),
            t => Results.Ok(TransactionResponse.From(t)));
    }

    /// <summary>Cancela sem excluir. Idempotente: cancelar de novo devolve 200 com o mesmo estado.</summary>
    private static async Task<IResult> Cancel(
        Guid workspaceId, Guid transactionId, CurrentUser currentUser, FinancialTransactionService transactions,
        CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        return ToResponse(
            await transactions.CancelAsync(workspaceId, transactionId, userId, cancellationToken),
            t => Results.Ok(TransactionResponse.From(t)));
    }

    private static IResult ToResponse(TransactionResult result, Func<FinancialTransactionDto, IResult> ok) => result.Access switch
    {
        TransactionAccess.NotFound => Results.NotFound(),
        TransactionAccess.Invalid => Results.ValidationProblem(result.Errors!),
        TransactionAccess.Conflict => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: result.Message),
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

/// <summary>Nova categoria de um lançamento importado; nula remove a categoria. Qualquer outro campo é recusado.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CategoryRequest(Guid? CategoryId);

/// <summary>
/// Lançamento como resposta da API: os mesmos campos do DTO, com <c>amount</c> como texto decimal exato com duas
/// casas ("1234.56"), padrão Money já usado em transferências e saldo. Nunca número JSON.
/// </summary>
public sealed record TransactionResponse(
    Guid Id,
    CategoryType Type,
    string Amount,
    DateOnly TransactionDate,
    string Description,
    TransactionReferenceDto Account,
    TransactionReferenceDto? Category,
    bool IsCancelled,
    string Currency,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? CancelledAt,
    Guid? PlannedTransactionId,
    FinancialTransactionOrigin Origin,
    Saldo360.Domain.Pluggy.PluggyCategorySource? CategorySource = null,
    bool CategoryNeedsReview = false)
{
    public static TransactionResponse From(FinancialTransactionDto dto) => new(
        dto.Id,
        dto.Type,
        Money.Format(dto.Amount),
        dto.TransactionDate,
        dto.Description,
        dto.Account,
        dto.Category,
        dto.IsCancelled,
        dto.Currency,
        dto.CreatedAt,
        dto.UpdatedAt,
        dto.CancelledAt,
        dto.PlannedTransactionId,
        dto.Origin,
        dto.CategorySource,
        dto.CategoryNeedsReview);
}

public sealed record TransactionPageResponse(IReadOnlyList<TransactionResponse> Items, int Page, int PageSize, int TotalCount)
{
    public static TransactionPageResponse From(TransactionPage page) =>
        new(page.Items.Select(TransactionResponse.From).ToList(), page.Page, page.PageSize, page.TotalCount);
}
