using System.Text.Json.Serialization;
using Saldo360.Api.Authentication;
using Saldo360.Application.CreditCards;
using Saldo360.Domain.CreditCards;

namespace Saldo360.Api.CreditCards;

public static class FinancialCreditCardPurchaseEndpoints
{
    public static IEndpointRouteBuilder MapFinancialCreditCardPurchaseEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/financial-workspaces/{workspaceId:guid}/credit-cards/{cardId:guid}/purchases").RequireAuthorization();
        group.MapPost("/", Create).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapPost("/preview", Preview).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapPut("/{purchaseId:guid}", Update).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapGet("/", List);
        group.MapGet("/{purchaseId:guid}", Get);
        group.MapPost("/{purchaseId:guid}/cancel", Cancel).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapPut("/{purchaseId:guid}/expense-nature", ChangeNature).AddEndpointFilter<ValidateCsrfTokenFilter>();
        group.MapPut("/{purchaseId:guid}/category", ChangeCategory).AddEndpointFilter<ValidateCsrfTokenFilter>();
        return app;
    }

    private static async Task<IResult> Create(Guid workspaceId, Guid cardId, PurchaseRequest? request, CurrentUser user, FinancialCreditCardPurchaseService service, CancellationToken ct)
    {
        if (await user.GetUserIdAsync(ct) is not { } userId) return Results.Unauthorized();
        var result = await service.CreateAsync(workspaceId, cardId, userId, request?.ToInput() ?? new(null, null, null, null, null), ct);
        return result.Access switch { CreditCardPurchaseAccess.NotFound => Results.NotFound(), CreditCardPurchaseAccess.Invalid => Results.ValidationProblem(result.Errors!), CreditCardPurchaseAccess.Conflict => Results.Json(new { errors = result.Errors }, statusCode: 409), _ => Results.Created($"/api/financial-workspaces/{workspaceId}/credit-cards/{cardId}/purchases/{result.Purchase!.Id}", PurchaseResponse.From(result.Purchase)) };
    }

    private static async Task<IResult> Update(Guid workspaceId, Guid cardId, Guid purchaseId, PurchaseRequest? request, CurrentUser user, FinancialCreditCardPurchaseService service, CancellationToken ct)
    {
        if (await user.GetUserIdAsync(ct) is not { } userId) return Results.Unauthorized();
        var result = await service.UpdateAsync(workspaceId, cardId, purchaseId, userId, request?.ToInput() ?? new(null, null, null, null, null), ct);
        return result.Access switch { CreditCardPurchaseAccess.NotFound => Results.NotFound(), CreditCardPurchaseAccess.Invalid => Results.ValidationProblem(result.Errors!), CreditCardPurchaseAccess.Conflict => Results.Json(new { errors = result.Errors }, statusCode: 409), _ => Results.Ok(PurchaseResponse.From(result.Purchase!)) };
    }

    private static async Task<IResult> Preview(Guid workspaceId, Guid cardId, PurchaseRequest? request, CurrentUser user, FinancialCreditCardPurchaseService service, CancellationToken ct)
    {
        if (await user.GetUserIdAsync(ct) is not { } userId) return Results.Unauthorized();
        try
        {
            var result = await service.PreviewAsync(workspaceId, cardId, userId, request?.ToInput() ?? new(null, null, null, null, null), ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }
        catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["purchase"] = [ex.Message] }); }
    }

    private static async Task<IResult> List(Guid workspaceId, Guid cardId, string? from, string? through, CurrentUser user, FinancialCreditCardPurchaseService service, CancellationToken ct)
    {
        if (await user.GetUserIdAsync(ct) is not { } userId) return Results.Unauthorized();
        var errors = new Dictionary<string, string[]>(); var start = Parse(from, "from", errors); var end = Parse(through, "through", errors);
        if (start is not null && end is not null && end < start) errors["through"] = ["through deve ser maior ou igual a from."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);
        var list = await service.ListAsync(workspaceId, cardId, userId, start, end, ct);
        return list is null ? Results.NotFound() : Results.Ok(list.Select(PurchaseResponse.From));
    }

    private static async Task<IResult> Get(Guid workspaceId, Guid cardId, Guid purchaseId, CurrentUser user, FinancialCreditCardPurchaseService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized() : await service.GetAsync(workspaceId, cardId, purchaseId, userId, ct) is { } purchase ? Results.Ok(PurchaseResponse.From(purchase)) : Results.NotFound();

    /// <summary>Corrige só o tipo de despesa (fixa/variável/não definida) da compra; as parcelas herdam. Não toca valor, fatura nem categoria.</summary>
    private static async Task<IResult> ChangeNature(Guid workspaceId, Guid cardId, Guid purchaseId, PurchaseNatureRequest? request, CurrentUser user, FinancialCreditCardPurchaseService service, CancellationToken ct)
    {
        if (await user.GetUserIdAsync(ct) is not { } userId) return Results.Unauthorized();
        var result = await service.ChangeNatureAsync(workspaceId, cardId, purchaseId, userId, request?.ExpenseNature, ct);
        return result.Access switch { CreditCardPurchaseAccess.NotFound => Results.NotFound(), CreditCardPurchaseAccess.Invalid => Results.ValidationProblem(result.Errors!), CreditCardPurchaseAccess.Conflict => Results.Conflict(new { title = result.Errors?.Values.FirstOrDefault()?.FirstOrDefault() }), _ => Results.Ok(PurchaseResponse.From(result.Purchase!)) };
    }

    /// <summary>Corrige só a categoria da compra (inclusive a importada da Pluggy), sem tocar valor, parcelas ou fatura; a escolha sobrevive às sincronizações.</summary>
    private static async Task<IResult> ChangeCategory(Guid workspaceId, Guid cardId, Guid purchaseId, PurchaseCategoryRequest? request, CurrentUser user, FinancialCreditCardPurchaseService service, CancellationToken ct)
    {
        if (await user.GetUserIdAsync(ct) is not { } userId) return Results.Unauthorized();
        var result = await service.ChangeCategoryAsync(workspaceId, cardId, purchaseId, userId, request?.CategoryId, ct);
        return result.Access switch
        {
            CreditCardPurchaseAccess.NotFound => Results.NotFound(),
            CreditCardPurchaseAccess.Invalid => Results.ValidationProblem(result.Errors!),
            CreditCardPurchaseAccess.Conflict => Results.Conflict(new { title = result.Errors?.Values.SelectMany(v => v).FirstOrDefault() }),
            _ => Results.Ok(PurchaseResponse.From(result.Purchase!)),
        };
    }

    private static async Task<IResult> Cancel(Guid workspaceId, Guid cardId, Guid purchaseId, CurrentUser user, FinancialCreditCardPurchaseService service, CancellationToken ct) =>
        await user.GetUserIdAsync(ct) is not { } userId ? Results.Unauthorized() : await service.CancelAsync(workspaceId, cardId, purchaseId, userId, ct) ? Results.NoContent() : Results.NotFound();

    private static DateOnly? Parse(string? value, string field, Dictionary<string, string[]> errors) => IsoDateQuery.Parse(value, field, errors);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PurchaseRequest(string? Description, DateOnly? PurchaseDate, decimal? TotalAmount, int? InstallmentCount, Guid? CategoryId,
    string AmountMode = "Total", int PreviouslyPaidInstallments = 0, DateOnly? FirstPendingCompetence = null, string? ExpenseNature = null)
{
    public CreditCardPurchaseInput ToInput() => new(Description, PurchaseDate, TotalAmount, InstallmentCount, CategoryId, AmountMode, PreviouslyPaidInstallments, FirstPendingCompetence, ExpenseNature);
}

/// <summary>Novo tipo de despesa da compra (<c>Fixed</c>, <c>Variable</c> ou <c>Undefined</c>); qualquer outro campo é recusado.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PurchaseNatureRequest(string? ExpenseNature);

/// <summary>Nova categoria da compra; qualquer outro campo é recusado.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PurchaseCategoryRequest(Guid? CategoryId);

public sealed record InstallmentResponse(Guid Id, int Number, string Amount, CreditCardInstallmentStatus Status);
public sealed record PurchaseResponse(Guid Id, Guid CreditCardId, Guid CategoryId, string Description, DateOnly PurchaseDate, string TotalAmount, int InstallmentCount, CreditCardPurchaseStatus Status, IReadOnlyList<InstallmentResponse> Installments, int PreviouslyPaidInstallments, DateOnly? FirstPendingCompetence, string ExpenseNature = "Undefined")
{
    public static PurchaseResponse From(FinancialCreditCardPurchaseDto p) => new(p.Id, p.CreditCardId, p.CategoryId, p.Description, p.PurchaseDate, p.TotalAmount, p.InstallmentCount, p.Status, p.Installments.Select(i => new InstallmentResponse(i.Id, i.Number, i.Amount, i.Status)).ToList(), p.PreviouslyPaidInstallments, p.FirstPendingCompetence, p.Nature.ToString());
}
