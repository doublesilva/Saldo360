using System.Text.Json.Serialization;
using Saldo360.Api.Authentication;
using Saldo360.Application.Accounts;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Common;

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
        accounts.MapGet("/{accountId:guid}/balance", GetBalance);
        return app;
    }

    /// <summary>
    /// Saldo realizado no fechamento de <c>asOf</c> (yyyy-MM-dd; padrão: hoje em America/Sao_Paulo). Valores
    /// como texto decimal exato. Indisponível (sem saldo inicial ou antes da referência): totais e saldo nulos.
    /// </summary>
    private static async Task<IResult> GetBalance(
        Guid familyId, Guid accountId, string? asOf, CurrentUser currentUser, FinancialAccountService accounts,
        CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        var errors = new Dictionary<string, string[]>();
        var date = IsoDateQuery.Parse(asOf, "asOf", errors);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var result = await accounts.GetBalanceAsync(familyId, accountId, userId, date, cancellationToken);
        return result.Access switch
        {
            BalanceAccess.NotFound => Results.NotFound(),
            BalanceAccess.Invalid => Results.ValidationProblem(result.Errors!),
            _ => Results.Ok(AccountBalanceResponse.From(result.Balance!)),
        };
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
            _ => Results.Created($"/api/families/{familyId}/accounts/{result.Account!.Id}", AccountResponse.From(result.Account)),
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
            ? Results.Ok(list.Select(AccountResponse.From).ToList())
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
            ? Results.Ok(AccountResponse.From(account))
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
            _ => Results.Ok(AccountResponse.From(result.Account!)),
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

/// <summary>
/// Saldo realizado. Valores monetários como texto decimal exato com duas casas ("1300.00"), nunca number JSON,
/// para não passarem por ponto flutuante no cliente. unavailableReason preenchido ⇔ totais e saldo nulos.
/// </summary>
public sealed record AccountBalanceResponse(
    Guid AccountId,
    DateOnly AsOf,
    string? OpeningBalance,
    DateOnly? OpeningBalanceDate,
    string? TotalIncome,
    string? TotalExpense,
    string? TotalTransferIn,
    string? TotalTransferOut,
    string? Balance,
    BalanceUnavailableReason? UnavailableReason)
{
    public static AccountBalanceResponse From(AccountBalanceDto dto) => new(
        dto.AccountId,
        dto.AsOf,
        Format(dto.OpeningBalance),
        dto.OpeningBalanceDate,
        Format(dto.TotalIncome),
        Format(dto.TotalExpense),
        Format(dto.TotalTransferIn),
        Format(dto.TotalTransferOut),
        Format(dto.Balance),
        dto.UnavailableReason);

    private static string? Format(decimal? amount) => amount is { } value ? Money.Format(value) : null;
}

/// <summary>
/// Conta como resposta da API: os mesmos campos do DTO, com <c>openingBalance</c> como texto decimal exato com duas
/// casas ("-1234.56") ou null (não informado, nunca zero). Padrão Money do saldo, lançamentos e transferências.
/// </summary>
public sealed record AccountResponse(
    Guid Id,
    string Name,
    string? Institution,
    AccountType Type,
    FinancialScope FinancialScope,
    AccountResponsibleDto? Responsible,
    string? OpeningBalance,
    DateOnly? OpeningBalanceDate,
    bool IsActive,
    string Currency,
    DateTimeOffset CreatedAt)
{
    public static AccountResponse From(FinancialAccountDto dto) => new(
        dto.Id,
        dto.Name,
        dto.Institution,
        dto.Type,
        dto.FinancialScope,
        dto.Responsible,
        dto.OpeningBalance is { } amount ? Money.Format(amount) : null,
        dto.OpeningBalanceDate,
        dto.IsActive,
        dto.Currency,
        dto.CreatedAt);
}
