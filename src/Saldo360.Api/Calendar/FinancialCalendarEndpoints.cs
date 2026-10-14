using Saldo360.Api.Authentication;
using Saldo360.Application.Accounts;
using Saldo360.Application.Calendar;
using Saldo360.Application.Transactions;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;

namespace Saldo360.Api.Calendar;

public static class FinancialCalendarEndpoints
{
    public static IEndpointRouteBuilder MapFinancialCalendarEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/families/{familyId:guid}/financial-calendar", List).RequireAuthorization();
        app.MapGet("/api/families/{familyId:guid}/financial-calendar/balances", ListBalances).RequireAuthorization();
        return app;
    }

    /// <summary>
    /// Evolução diária do saldo. Histórico usa somente realizados; hoje e futuro usam a projeção da T06.3.
    /// Sem accountId, consolida todas as contas e elimina transferências internas.
    /// </summary>
    private static async Task<IResult> ListBalances(
        Guid familyId,
        string? from,
        string? through,
        Guid? accountId,
        CurrentUser currentUser,
        FinancialCalendarBalanceService balances,
        CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        var errors = new Dictionary<string, string[]>();
        var fromDate = ParseRequired(from, "from", "Informe a data inicial.", errors);
        var throughDate = ParseRequired(through, "through", "Informe a data final.", errors);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var result = await balances.GetAsync(
            familyId,
            userId,
            new FinancialCalendarBalanceQuery(fromDate!.Value, throughDate!.Value, accountId),
            cancellationToken);
        return result switch
        {
            { NotFound: true } => Results.NotFound(),
            { Errors: { } validationErrors } => Results.ValidationProblem(validationErrors),
            _ => Results.Ok(FinancialCalendarBalancesResponse.Create(result.Balances!)),
        };
    }

    /// <summary>
    /// Agenda cronológica inclusiva. from/through são obrigatórios em yyyy-MM-dd; accountId e type
    /// (Income/Expense) são opcionais. Realizações aparecem somente pelo lançamento efetivo.
    /// </summary>
    private static async Task<IResult> List(
        Guid familyId,
        string? from,
        string? through,
        Guid? accountId,
        string? type,
        CurrentUser currentUser,
        FinancialCalendarService calendar,
        CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        var errors = new Dictionary<string, string[]>();
        var fromDate = ParseRequired(from, "from", "Informe a data inicial.", errors);
        var throughDate = ParseRequired(through, "through", "Informe a data final.", errors);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var result = await calendar.ListAsync(
            familyId,
            userId,
            new FinancialCalendarQuery(fromDate!.Value, throughDate!.Value, accountId, type),
            cancellationToken);
        return result switch
        {
            { NotFound: true } => Results.NotFound(),
            { Errors: { } validationErrors } => Results.ValidationProblem(validationErrors),
            _ => Results.Ok(FinancialCalendarResponse.Create(result.Calendar!)),
        };
    }

    private static DateOnly? ParseRequired(
        string? value,
        string field,
        string requiredMessage,
        Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrEmpty(value))
        {
            errors[field] = [requiredMessage];
            return null;
        }

        return IsoDateQuery.Parse(value, field, errors);
    }
}

public sealed record FinancialCalendarResponse(
    DateOnly From,
    DateOnly Through,
    IReadOnlyList<FinancialCalendarItemResponse> Items,
    FinancialCalendarSummaryResponse Summary)
{
    public static FinancialCalendarResponse Create(FinancialCalendar calendar) =>
        new(
            calendar.From,
            calendar.Through,
            calendar.Items.Select(FinancialCalendarItemResponse.From).ToList(),
            FinancialCalendarSummaryResponse.From(calendar.Summary));
}

public sealed record FinancialCalendarSummaryResponse(
    string RealizedIncome,
    string RealizedExpense,
    string PendingIncome,
    string PendingExpense,
    string OverdueIncome,
    string OverdueExpense)
{
    public static FinancialCalendarSummaryResponse From(FinancialCalendarSummaryDto summary) => new(
        Money.Format(summary.RealizedIncome),
        Money.Format(summary.RealizedExpense),
        Money.Format(summary.PendingIncome),
        Money.Format(summary.PendingExpense),
        Money.Format(summary.OverdueIncome),
        Money.Format(summary.OverdueExpense));
}

public sealed record FinancialCalendarBalancesResponse(
    DateOnly From,
    DateOnly Through,
    Guid? AccountId,
    string? OpeningBalance,
    BalanceUnavailableReason? UnavailableReason,
    IReadOnlyList<FinancialCalendarBalanceDayResponse> Days,
    FinancialCalendarBalanceRiskResponse Risk)
{
    public static FinancialCalendarBalancesResponse Create(FinancialCalendarBalances balances) => new(
        balances.From,
        balances.Through,
        balances.AccountId,
        Format(balances.OpeningBalance),
        balances.UnavailableReason,
        balances.Days.Select(FinancialCalendarBalanceDayResponse.From).ToList(),
        FinancialCalendarBalanceRiskResponse.From(balances.Risk));

    internal static string? Format(decimal? amount) => amount is { } value ? Money.Format(value) : null;
}

public sealed record FinancialCalendarBalanceDayResponse(
    DateOnly Date,
    FinancialCalendarBalanceKind Kind,
    string? Income,
    string? Expense,
    string? TransferIn,
    string? TransferOut,
    string? ClosingBalance,
    bool HasOverduePending,
    bool IsNegative,
    int? ChartPosition,
    BalanceUnavailableReason? UnavailableReason)
{
    public static FinancialCalendarBalanceDayResponse From(FinancialCalendarBalanceDay day) => new(
        day.Date,
        day.Kind,
        FinancialCalendarBalancesResponse.Format(day.Income),
        FinancialCalendarBalancesResponse.Format(day.Expense),
        FinancialCalendarBalancesResponse.Format(day.TransferIn),
        FinancialCalendarBalancesResponse.Format(day.TransferOut),
        FinancialCalendarBalancesResponse.Format(day.ClosingBalance),
        day.HasOverduePending,
        day.IsNegative,
        day.ChartPosition,
        day.UnavailableReason);
}

public sealed record FinancialCalendarBalanceRiskResponse(
    string? MinimumProjectedBalance,
    DateOnly? FirstNegativeProjectedDate,
    string? EndingProjectedBalance)
{
    public static FinancialCalendarBalanceRiskResponse From(FinancialCalendarBalanceRisk risk) => new(
        FinancialCalendarBalancesResponse.Format(risk.MinimumProjectedBalance),
        risk.FirstNegativeProjectedDate,
        FinancialCalendarBalancesResponse.Format(risk.EndingProjectedBalance));
}

public sealed record FinancialCalendarItemResponse(
    Guid Id,
    FinancialCalendarSource Source,
    FinancialCalendarStatus Status,
    CategoryType Type,
    string Amount,
    DateOnly Date,
    string Description,
    TransactionReferenceDto Account,
    TransactionReferenceDto Category,
    string Currency,
    Guid? PlannedTransactionId)
{
    public static FinancialCalendarItemResponse From(FinancialCalendarItemDto item) => new(
        item.Id,
        item.Source,
        item.Status,
        item.Type,
        Money.Format(item.Amount),
        item.Date,
        item.Description,
        item.Account,
        item.Category,
        item.Currency,
        item.PlannedTransactionId);
}
