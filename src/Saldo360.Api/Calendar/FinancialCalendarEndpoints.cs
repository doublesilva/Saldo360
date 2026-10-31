using System.Globalization;
using Saldo360.Api.Authentication;
using Saldo360.Application.Accounts;
using Saldo360.Application.Calendar;
using Saldo360.Application.Transactions;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;
using Saldo360.Domain.Transactions;

namespace Saldo360.Api.Calendar;

public static class FinancialCalendarEndpoints
{
    public static IEndpointRouteBuilder MapFinancialCalendarEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/financial-workspaces/{workspaceId:guid}/financial-calendar", List).RequireAuthorization();
        app.MapGet("/api/financial-workspaces/{workspaceId:guid}/financial-calendar/balances", ListBalances).RequireAuthorization();
        app.MapGet("/api/financial-workspaces/{workspaceId:guid}/financial-calendar/monthly-projection", ListMonthlyProjection)
            .RequireAuthorization();
        app.MapGet("/api/financial-workspaces/{workspaceId:guid}/financial-planning/six-months", ListSixMonths).RequireAuthorization();
        return app;
    }

    private static async Task<IResult> ListSixMonths(Guid workspaceId, string? from, Guid? accountId,
        CurrentUser currentUser, FinancialCalendarMonthlyProjectionService projections, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        var today = BusinessCalendar.Today(clock.GetUtcNow());
        var first = new DateOnly(today.Year, today.Month, 1);
        if (from is not null && (from.Length != 7 || !DateOnly.TryParseExact($"{from}-01", "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out first)))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["from"] = ["Use uma competência válida no formato yyyy-MM."] });
        var result = await projections.GetAsync(workspaceId, userId, new(first, 6, accountId), cancellationToken, today);
        return result switch
        {
            { NotFound: true } => Results.NotFound(),
            { Errors: { } errors } => Results.ValidationProblem(errors),
            _ => Results.Ok(FinancialCalendarMonthlyProjectionResponse.Create(result.Projection!)),
        };
    }

    private static async Task<IResult> ListMonthlyProjection(
        Guid workspaceId,
        string? from,
        int? months,
        Guid? accountId,
        CurrentUser currentUser,
        FinancialCalendarMonthlyProjectionService projections,
        CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId)
        {
            return Results.Unauthorized();
        }

        var errors = new Dictionary<string, string[]>();
        DateOnly? firstMonth = null;
        if (string.IsNullOrEmpty(from))
        {
            errors["from"] = ["Informe a competência inicial."];
        }
        else if (from.Length != 7 || !DateOnly.TryParseExact(
            $"{from}-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            errors["from"] = ["Use uma competência válida no formato yyyy-MM."];
        }
        else
        {
            firstMonth = parsed;
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var result = await projections.GetAsync(
            workspaceId,
            userId,
            new FinancialCalendarMonthlyProjectionQuery(firstMonth!.Value, months ?? 6, accountId),
            cancellationToken);
        return result switch
        {
            { NotFound: true } => Results.NotFound(),
            { Errors: { } validationErrors } => Results.ValidationProblem(validationErrors),
            _ => Results.Ok(FinancialCalendarMonthlyProjectionResponse.Create(result.Projection!)),
        };
    }

    /// <summary>
    /// Evolução diária do saldo. Histórico usa somente realizados; hoje e futuro usam a projeção da T06.3.
    /// Sem accountId, consolida todas as contas e elimina transferências internas.
    /// </summary>
    private static async Task<IResult> ListBalances(
        Guid workspaceId,
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
            workspaceId,
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
        Guid workspaceId,
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
            workspaceId,
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
    /// <summary>Devoluções bancárias do período, já abatidas de <c>RealizedExpense</c> e fora de <c>RealizedIncome</c>.</summary>
    public string RealizedRefunds { get; init; } = "0.00";

    public static FinancialCalendarSummaryResponse From(FinancialCalendarSummaryDto summary) => new(
        Money.Format(summary.RealizedIncome),
        Money.Format(summary.RealizedExpense),
        Money.Format(summary.PendingIncome),
        Money.Format(summary.PendingExpense),
        Money.Format(summary.OverdueIncome),
        Money.Format(summary.OverdueExpense))
    { RealizedRefunds = Money.Format(summary.RealizedRefunds) };
}

public sealed record FinancialCalendarBalancesResponse(
    DateOnly From,
    DateOnly Through,
    Guid? AccountId,
    string? OpeningBalance,
    BalanceUnavailableReason? UnavailableReason,
    IReadOnlyList<FinancialCalendarBalanceDayResponse> Days,
    FinancialCalendarBalanceRiskResponse Risk,
    bool CreditCardProjectionComplete = true,
    string? CreditCardOutstanding = null,
    bool DebtProjectionComplete = true,
    string? DebtInstallmentOutstanding = null)
{
    public static FinancialCalendarBalancesResponse Create(FinancialCalendarBalances balances) => new(
        balances.From,
        balances.Through,
        balances.AccountId,
        Format(balances.OpeningBalance),
        balances.UnavailableReason,
        balances.Days.Select(FinancialCalendarBalanceDayResponse.From).ToList(),
        FinancialCalendarBalanceRiskResponse.From(balances.Risk),
        balances.CreditCardProjectionComplete,
        Format(balances.CreditCardOutstanding),
        balances.DebtProjectionComplete,
        Format(balances.DebtInstallmentOutstanding));

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

public sealed record FinancialCalendarMonthlyProjectionResponse(
    string From,
    IReadOnlyList<FinancialCalendarMonthlyProjectionMonthResponse> Months,
    string Through,
    FinancialPlanningSummary Summary)
{
    public static FinancialCalendarMonthlyProjectionResponse Create(FinancialCalendarMonthlyProjection projection) =>
        new(projection.From.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            projection.Months.Select(FinancialCalendarMonthlyProjectionMonthResponse.From).ToList(),
            projection.Through.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            new(Money.Format(projection.TotalIncome), Money.Format(projection.TotalExpense),
                FinancialCalendarBalancesResponse.Format(projection.LowestClosingBalance),
                projection.LowestBalanceMonth?.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                projection.MonthsOverBudget, projection.MonthsWithNegativeBalance));
}

public sealed record FinancialPlanningSummary(string TotalIncome, string TotalExpense, string? LowestClosingBalance,
    string? LowestBalanceMonth, int MonthsOverBudget, int MonthsWithNegativeBalance);

public sealed record FinancialCalendarMonthlyProjectionMonthResponse(
    string Month,
    string RealizedIncome,
    string RealizedExpense,
    string PendingIncome,
    string PendingExpense,
    string OverdueIncome,
    string OverdueExpense,
    string? OpeningBalance,
    string? ClosingBalance,
    string? MinimumProjectedBalance,
    DateOnly? FirstNegativeProjectedDate,
    BalanceUnavailableReason? UnavailableReason,
    bool CreditCardProjectionComplete = true,
    string? CreditCardOutstanding = null,
    string CardPayments = "0.00",
    string TotalIncome = "0.00",
    string TotalExpense = "0.00",
    string? BudgetTarget = null,
    string? BudgetConsumption = null,
    string BudgetStatus = "NotDefined",
    string PeriodKind = "Future",
    string Status = "WithinKnownCommitments",
    string? FixedExpenses = null,
    bool DebtProjectionComplete = true)
{
    public static FinancialCalendarMonthlyProjectionMonthResponse From(
        FinancialCalendarMonthlyProjectionMonth month) => new(
            month.Month.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            Money.Format(month.RealizedIncome),
            Money.Format(month.RealizedExpense),
            Money.Format(month.PendingIncome),
            Money.Format(month.PendingExpense),
            Money.Format(month.OverdueIncome),
            Money.Format(month.OverdueExpense),
            FinancialCalendarBalancesResponse.Format(month.OpeningBalance),
            FinancialCalendarBalancesResponse.Format(month.ClosingBalance),
            FinancialCalendarBalancesResponse.Format(month.MinimumProjectedBalance),
            month.FirstNegativeProjectedDate,
            month.UnavailableReason,
            month.CreditCardProjectionComplete,
            FinancialCalendarBalancesResponse.Format(month.CreditCardOutstanding), Money.Format(month.CardPayments),
            Money.Format(month.TotalIncome), Money.Format(month.TotalExpense),
            FinancialCalendarBalancesResponse.Format(month.BudgetTarget), FinancialCalendarBalancesResponse.Format(month.BudgetConsumption),
            month.BudgetStatus, month.PeriodKind, month.Status, null, month.DebtProjectionComplete);
}

public sealed record FinancialCalendarItemResponse(
    Guid Id,
    FinancialCalendarSource Source,
    FinancialCalendarStatus Status,
    CategoryType Type,
    string Amount,
    DateOnly Date,
    string Description,
    TransactionReferenceDto? Account,
    TransactionReferenceDto? Category,
    string Currency,
    Guid? PlannedTransactionId,
    FinancialTransactionOrigin Origin,
    Guid? RecurrenceId,
    int? OccurrenceNumber,
    DateOnly? ScheduledDate,
    IReadOnlyList<string> AllowedActions)
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
        item.PlannedTransactionId,
        item.Origin,
        item.RecurrenceId,
        item.OccurrenceNumber,
        item.ScheduledDate,
        item.AllowedActions);
}
