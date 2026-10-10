using System.Text.Json.Serialization;
using Saldo360.Api.Authentication;
using Saldo360.Application.Accounts;

namespace Saldo360.Api.Accounts;

public static class FinancialAccountEndpoints
{
    public static IEndpointRouteBuilder MapFinancialAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var accounts = app.MapGroup("/api/families/{familyId:guid}/accounts").RequireAuthorization();
        accounts.MapPost("/", Create).AddEndpointFilter<ValidateCsrfTokenFilter>();
        accounts.MapGet("/", List);
        accounts.MapGet("/{accountId:guid}", Get);
        accounts.MapPut("/{accountId:guid}", Update).AddEndpointFilter<ValidateCsrfTokenFilter>();
        return app;
    }

    private static async Task<IResult> Create(
        Guid familyId, CreateAccountRequest? request, CurrentUser currentUser, FinancialAccountService accounts,
        CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        var result = await accounts.CreateAsync(familyId, userId, ToInput(request), cancellationToken);
        return result.Access switch
        {
            AccountAccess.NotFound => Results.NotFound(),
            AccountAccess.Invalid => Results.ValidationProblem(result.Errors!),
            _ => Results.Created($"/api/families/{familyId}/accounts/{result.Account!.Id}", result.Account),
        };
    }

    /// <summary>Contas ativas; com <c>includeInactive=true</c>, também as desativadas.</summary>
    private static async Task<IResult> List(
        Guid familyId, bool? includeInactive, CurrentUser currentUser, FinancialAccountService accounts,
        CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        return await accounts.ListAsync(familyId, userId, includeInactive ?? false, cancellationToken) is { } list
            ? Results.Ok(list)
            : Results.NotFound();
    }

    private static async Task<IResult> Get(
        Guid familyId, Guid accountId, CurrentUser currentUser, FinancialAccountService accounts,
        CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        return await accounts.GetAsync(familyId, accountId, userId, cancellationToken) is { } account
            ? Results.Ok(account)
            : Results.NotFound();
    }

    /// <summary>Substitui os campos cadastrais e a situação (isActive obrigatório).</summary>
    private static async Task<IResult> Update(
        Guid familyId, Guid accountId, UpdateAccountRequest? request, CurrentUser currentUser,
        FinancialAccountService accounts, CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        var input = new AccountInput(
            request?.Name, request?.Institution, request?.Type, request?.FinancialScope, request?.ResponsibleMemberId,
            request?.OpeningBalance, request?.OpeningBalanceDate);
        var result = await accounts.UpdateAsync(familyId, accountId, userId, input, request?.IsActive, cancellationToken);
        return result.Access switch
        {
            AccountAccess.NotFound => Results.NotFound(),
            AccountAccess.Invalid => Results.ValidationProblem(result.Errors!),
            _ => Results.Ok(result.Account),
        };
    }

    private static AccountInput ToInput(CreateAccountRequest? request) =>
        new(request?.Name, request?.Institution, request?.Type, request?.FinancialScope, request?.ResponsibleMemberId,
            request?.OpeningBalance, request?.OpeningBalanceDate);
}

/// <summary>
/// Cadastro de conta. Família vem da rota e criador da sessão; campos como familyId, createdByUserId,
/// isActive ou currency são recusados (400). Agência, número e credenciais bancárias não existem aqui.
/// Saldo inicial opcional: <c>openingBalance</c> (número JSON, até duas casas) e <c>openingBalanceDate</c>
/// (<c>yyyy-MM-dd</c>, sem horário), juntos ou ambos ausentes/nulos.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateAccountRequest(
    string? Name,
    string? Institution,
    string? Type,
    string? FinancialScope,
    Guid? ResponsibleMemberId,
    decimal? OpeningBalance,
    DateOnly? OpeningBalanceDate);

/// <summary>
/// Edição (substituição completa): os mesmos campos cadastrais mais a situação. Saldo inicial ausente ou nulo
/// remove a configuração. familyId e createdByUserId são recusados.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateAccountRequest(
    string? Name,
    string? Institution,
    string? Type,
    string? FinancialScope,
    Guid? ResponsibleMemberId,
    decimal? OpeningBalance,
    DateOnly? OpeningBalanceDate,
    bool? IsActive);
