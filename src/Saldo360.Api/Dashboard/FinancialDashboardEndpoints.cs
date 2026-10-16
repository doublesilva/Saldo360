using System.Globalization;
using Saldo360.Api.Authentication;
using Saldo360.Application.Dashboard;
using Saldo360.Application.Calendar;
using Saldo360.Domain.Common;

namespace Saldo360.Api.Dashboard;

public static class FinancialDashboardEndpoints
{
    public static IEndpointRouteBuilder MapFinancialDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/families/{familyId:guid}/financial-dashboard", Get).RequireAuthorization();
        return app;
    }

    private static async Task<IResult> Get(
        Guid familyId,
        string? competence,
        int? months,
        CurrentUser currentUser,
        FinancialDashboardService service,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (await currentUser.GetUserIdAsync(cancellationToken) is not { } userId) return Results.Unauthorized();
        var errors = new Dictionary<string, string[]>();
        DateOnly month;
        if (string.IsNullOrWhiteSpace(competence))
        {
            month = BusinessCalendar.Today(timeProvider.GetUtcNow());
            month = new DateOnly(month.Year, month.Month, 1);
        }
        else if (competence.Length != 7 || !DateOnly.TryParseExact($"{competence}-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out month))
        {
            errors["competence"] = ["Use uma competência válida no formato yyyy-MM."];
            month = default;
        }
        var horizon = months ?? 6;
        if (horizon is < 1 or > 12) errors["months"] = ["A quantidade de meses deve estar entre 1 e 12."];
        if (errors.Count > 0) return Results.ValidationProblem(errors);
        var result = await service.GetAsync(familyId, userId, month, horizon, cancellationToken);
        return result switch
        {
            { NotFound: true } => Results.NotFound(),
            { Errors: { } validation } => Results.ValidationProblem(validation),
            _ => Results.Ok(FinancialDashboardResponse.From(result.Dashboard!)),
        };
    }
}

public sealed record FinancialDashboardResponse(
    string Competence, int Months, FinancialDashboardPositionResponse Position,
    FinancialDashboardCashFlowResponse CashFlow, FinancialDashboardObligationsResponse Obligations,
    FinancialDashboardBudgetsResponse Budgets, FinancialDashboardCreditCardsResponse CreditCards,
    FinancialDashboardDebtsResponse Debts, FinancialDashboardGoalsResponse Goals,
    IReadOnlyList<FinancialDashboardMonthlyProjectionResponse> MonthlyProjection,
    FinancialDashboardCoverageResponse Coverage, FinancialDashboardIndicatorsResponse Indicators,
    IReadOnlyList<FinancialDashboardAlertResponse> Alerts, DateOnly AsOf)
{
    public static FinancialDashboardResponse From(FinancialDashboard x) => new(
        x.Competence.ToString("yyyy-MM", CultureInfo.InvariantCulture), x.Months,
        new(x.Position.RealizedBalance is { } b ? Money.Format(b) : null, x.Position.Accounts.Select(a => new FinancialDashboardAccountBalanceResponse(a.AccountId, a.Name, a.RealizedBalance is { } v ? Money.Format(v) : null, a.Complete)).ToList(), x.Position.ConsideredAccounts, Money.Format(x.Position.NegativeBalance), Money.Format(x.Position.AllocatedToGoals), x.Position.UnallocatedRealizedBalance is { } u ? Money.Format(u) : null),
        new(Money.Format(x.CashFlow.RealizedIncome), Money.Format(x.CashFlow.RealizedExpense), Money.Format(x.CashFlow.PendingIncome), Money.Format(x.CashFlow.PendingExpense)),
        new(Money.Format(x.Obligations.PendingIncome), Money.Format(x.Obligations.PendingExpense), Money.Format(x.Obligations.CreditCardOutstanding), Money.Format(x.Obligations.DebtOutstanding), x.Obligations.PendingItems),
        new(x.Budgets.TargetCount, Money.Format(x.Budgets.Planned), Money.Format(x.Budgets.Committed), Money.Format(x.Budgets.Realized), x.Budgets.ExceededCount, x.Budgets.AttentionCount),
        new(x.CreditCards.Count, Money.Format(x.CreditCards.Outstanding), x.CreditCards.OverdueInvoices, x.CreditCards.FutureInvoices, x.CreditCards.HasUnallocatedObligations, x.CreditCards.AvailableLimit is { } l ? Money.Format(l) : null),
        new(x.Debts.ActiveContracts, x.Debts.OverdueInstallments, Money.Format(x.Debts.InstallmentsDueInHorizon), x.Debts.DeclaredOutstandingBalance is { } d ? Money.Format(d) : null, x.Debts.UnallocatedObligations),
        new(x.Goals.Active, x.Goals.Completed, x.Goals.Paused, Money.Format(x.Goals.Allocated), Money.Format(x.Goals.Unallocated), x.Goals.CoverageComplete),
        x.MonthlyProjection.Select(m => new FinancialDashboardMonthlyProjectionResponse(m.Competence.ToString("yyyy-MM", CultureInfo.InvariantCulture), m.ClosingBalance is { } c ? Money.Format(c) : null, Money.Format(m.RealizedIncome), Money.Format(m.RealizedExpense), Money.Format(m.PendingIncome), Money.Format(m.PendingExpense), Money.Format(m.CreditCardOutstanding), m.ProjectionComplete)).ToList(),
        new(x.Coverage.Complete, x.Coverage.HasUnallocatedCreditCardObligations, x.Coverage.HasUnallocatedDebtObligations, x.Coverage.Reason),
        FinancialDashboardIndicatorsResponse.From(x.Indicators),
        x.Alerts.Select(FinancialDashboardAlertResponse.From).ToList(), x.AsOf);
}

public sealed record FinancialDashboardPositionResponse(string? RealizedBalance, IReadOnlyList<FinancialDashboardAccountBalanceResponse> Accounts, int ConsideredAccounts, string NegativeBalance, string AllocatedToGoals, string? UnallocatedRealizedBalance);
public sealed record FinancialDashboardAccountBalanceResponse(Guid AccountId, string Name, string? RealizedBalance, bool Complete);
public sealed record FinancialDashboardCashFlowResponse(string RealizedIncome, string RealizedExpense, string PendingIncome, string PendingExpense);
public sealed record FinancialDashboardObligationsResponse(string PendingIncome, string PendingExpense, string CreditCardOutstanding, string DebtOutstanding, int PendingItems);
public sealed record FinancialDashboardBudgetsResponse(int TargetCount, string Planned, string Committed, string Realized, int ExceededCount, int AttentionCount);
public sealed record FinancialDashboardCreditCardsResponse(int Count, string Outstanding, int OverdueInvoices, int FutureInvoices, bool HasUnallocatedObligations, string? AvailableLimit);
public sealed record FinancialDashboardDebtsResponse(int ActiveContracts, int OverdueInstallments, string InstallmentsDueInHorizon, string? DeclaredOutstandingBalance, int UnallocatedObligations);
public sealed record FinancialDashboardGoalsResponse(int Active, int Completed, int Paused, string Allocated, string Unallocated, bool CoverageComplete);
public sealed record FinancialDashboardMonthlyProjectionResponse(string Competence, string? ClosingBalance, string RealizedIncome, string RealizedExpense, string PendingIncome, string PendingExpense, string CreditCardOutstanding, bool ProjectionComplete);
public sealed record FinancialDashboardCoverageResponse(bool Complete, bool HasUnallocatedCreditCardObligations, bool HasUnallocatedDebtObligations, string? Reason);
public sealed record FinancialDashboardIndicatorsResponse(string? RealizedBalance, string? ProjectedBalance, string? ProjectedVariation, string? ProjectedVariationReference, string RealizedNet, string PendingNet, string KnownNet, string Commitment, string? UnallocatedAvailability, FinancialDashboardTrend Trend, FinancialDashboardNextCommitmentResponse? NextCommitment, FinancialDashboardCapacityBeforeIncomeResponse CapacityBeforeNextIncome)
{
    public static FinancialDashboardIndicatorsResponse From(FinancialDashboardIndicators x) => new(x.RealizedBalance is { } r ? Money.Format(r) : null, x.ProjectedBalance is { } p ? Money.Format(p) : null, x.ProjectedVariation is { } v ? Money.Format(v) : null, x.ProjectedVariationReference, Money.Format(x.RealizedNet), Money.Format(x.PendingNet), Money.Format(x.KnownNet), Money.Format(x.Commitment), x.UnallocatedAvailability is { } u ? Money.Format(u) : null, x.Trend, x.NextCommitment is null ? null : new(x.NextCommitment.Source, x.NextCommitment.Id, x.NextCommitment.Date, Money.Format(x.NextCommitment.Amount), x.NextCommitment.Description, x.NextCommitment.AccountId), new(x.CapacityBeforeNextIncome.Status, x.CapacityBeforeNextIncome.NextIncomeDate, x.CapacityBeforeNextIncome.NextIncomeAmount is { } amount ? Money.Format(amount) : null, x.CapacityBeforeNextIncome.ProjectedBalanceBeforeIncome is { } balance ? Money.Format(balance) : null, x.CapacityBeforeNextIncome.MinimumProjectedBalance is { } minimum ? Money.Format(minimum) : null));
}
public sealed record FinancialDashboardNextCommitmentResponse(FinancialCalendarSource Source, Guid Id, DateOnly Date, string Amount, string Description, Guid? AccountId);
public sealed record FinancialDashboardCapacityBeforeIncomeResponse(string Status, DateOnly? NextIncomeDate, string? NextIncomeAmount, string? ProjectedBalanceBeforeIncome, string? MinimumProjectedBalance);
public sealed record FinancialDashboardAlertResponse(string Code, FinancialDashboardAlertSeverity Severity, string Title, string Description, DateOnly? Competence, string? Amount, Guid? SourceId, string? Context, DateOnly ReferenceDate)
{
    public static FinancialDashboardAlertResponse From(FinancialDashboardAlert x) => new(x.Code, x.Severity, x.Title, x.Description, x.Competence, x.Amount is { } amount ? Money.Format(amount) : null, x.SourceId, x.Context, x.ReferenceDate);
}
