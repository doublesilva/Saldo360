using System.Globalization;
using Saldo360.Application.Accounts;
using Saldo360.Application.Budgets;
using Saldo360.Application.Calendar;
using Saldo360.Application.CreditCards.Projection;
using Saldo360.Application.Dashboard.PlanActual;
using Saldo360.Application.Workspaces;
using Saldo360.Domain.Budgets;
using Saldo360.Domain.Common;
using Saldo360.Domain.Planning;

namespace Saldo360.Application.MonthlyPlan;

/// <summary>
/// Planejamento mensal (T10.2): receitas esperadas, despesas planejadas, transferências previstas e limites por categoria, por competência e conta.
/// É META do orçamento: nada aqui gera lançamento, previsão, pagamento ou transferência, nem altera saldo ou projeção da Agenda. A realização e a projeção
/// vêm do Dashboard (T10.1) e da projeção mensal da Agenda (T07.5), que já tratam cartões, faturas, parcelas e transferências sem duplicar.
/// </summary>
public sealed partial class MonthlyPlanService(
    IFinancialWorkspaceRepository workspaces, IFinancialAccountRepository accounts, IFinancialBudgetRepository budgetRepository, FinancialBudgetService budgetService,
    FinancialBudgetExecutionService budgetExecution, IMonthlyPlanRepository repository, PlanActualService dashboard, FinancialCalendarMonthlyProjectionService monthlyProjection,
    CardProjectionService cardProjection, IPlanActualRepository planActualRepository, TimeProvider time)
{
    private const int HistoryLimit = 200;

    private async Task<bool> IsParticipantAsync(Guid workspaceId, Guid userId, CancellationToken ct) =>
        await workspaces.GetOwnerRoleAsync(workspaceId, userId, ct) is not null;

    private DateOnly Today => BusinessCalendar.Today(time.GetUtcNow());

    private static bool TryMonth(string? text, out DateOnly month)
    {
        month = default;
        return text is { Length: 7 } && DateOnly.TryParseExact(text + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out month);
    }

    private static string Key(DateOnly month) => month.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private static bool TryEnum<T>(string? value, out T result) where T : struct, Enum
    {
        result = default;
        return value is not null && Enum.GetNames<T>().Contains(value, StringComparer.Ordinal) && Enum.TryParse(value, false, out result);
    }

    private static bool TryDecimal(string? text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);

    private static MonthlyPlanResult<T> From<T>(PlanActualResult<PlanActualDashboardDto> failure) where T : class =>
        failure.Access == PlanActualAccess.Invalid ? MonthlyPlanResult<T>.Invalid(failure.Errors!) : MonthlyPlanResult<T>.NotFound;

    private static MonthlyPlanResult<T> From<T>(BudgetResult result) where T : class => result.Access switch
    {
        BudgetAccess.Invalid => MonthlyPlanResult<T>.Invalid(result.Errors!),
        BudgetAccess.Conflict => MonthlyPlanResult<T>.Conflict(result.Message!),
        _ => MonthlyPlanResult<T>.NotFound,
    };

    // ---- Consulta do mês -----------------------------------------------------------------------------------------------------------------

    /// <summary>O planejamento do mês no escopo escolhido (conta principal por padrão), junto com a realização e a projeção do Dashboard.</summary>
    public async Task<MonthlyPlanResult<MonthlyPlanDto>> GetAsync(Guid workspaceId, Guid userId, string? competence, string? scope, Guid? accountId, CancellationToken ct)
    {
        if (!await IsParticipantAsync(workspaceId, userId, ct)) return MonthlyPlanResult<MonthlyPlanDto>.NotFound;
        var month = new DateOnly(Today.Year, Today.Month, 1);
        if (!string.IsNullOrWhiteSpace(competence) && !TryMonth(competence, out month)) return MonthlyPlanResult<MonthlyPlanDto>.Invalid("competence", "Use uma competência válida no formato yyyy-MM.");

        var board = await dashboard.GetAsync(workspaceId, userId, Key(month), scope, accountId, ct);
        if (board.Access != PlanActualAccess.Allowed) return From<MonthlyPlanDto>(board);
        var d = board.Value!;
        var scopeAccount = d.Scope.Kind == PlanActualScopes.All ? null : d.Scope.AccountId;

        var facts = await repository.LoadAsync(workspaceId, month, month, ct);
        var totals = MonthlyPlanCalculator.Planned(facts, scopeAccount);
        var lookups = await LookupsAsync(workspaceId, facts, ct);
        var nonWorking = (await repository.ListNonWorkingDaysAsync(workspaceId, month, month.AddMonths(1).AddDays(-1), ct)).Select(x => x.Date).ToHashSet();
        var business = MonthlyPlanCalculator.CountDays(month, nonWorking).BusinessDays;

        var visible = facts.Lines.Where(l => scopeAccount is null || l.AccountId == scopeAccount || l.DestinationAccountId == scopeAccount).ToList();
        var lines = visible.Select(l => ToDto(l, lookups, business)).ToList();

        Dictionary<Guid, FinancialBudgetTargetExecutionDto> execution = [];
        if (facts.Limits.Count > 0 && await budgetExecution.GetAsync(workspaceId, facts.Limits[0].BudgetId, userId, ct) is { } executed)
            execution = executed.Targets.ToDictionary(t => t.TargetId);
        var limits = facts.Limits.Select(l =>
        {
            var itemized = MonthlyPlanCalculator.Itemized(facts, l.CategoryId);
            execution.TryGetValue(l.TargetId, out var run);
            return new CategoryLimitDto(l.TargetId, l.CategoryId, l.CategoryName, MonthlyPlanCalculator.Cents(l.Amount), l.OriginalAmount is { } o ? MonthlyPlanCalculator.Cents(o) : null,
                l.OriginalAmount is { } original && original != l.Amount, MonthlyPlanCalculator.Cents(itemized), itemized > l.Amount,
                run is null ? null : new LimitExecutionDto(MonthlyPlanCalculator.Cents(run.Amounts.Realized), MonthlyPlanCalculator.Cents(run.Amounts.Committed),
                    MonthlyPlanCalculator.Cents(run.Amounts.CreditCardCommitted), MonthlyPlanCalculator.Cents(run.Amounts.ProjectedConsumption), MonthlyPlanCalculator.Cents(run.Amounts.AvailableProjected),
                    MonthlyPlanCalculator.Cents(run.Amounts.ExceededProjected), run.Amounts.ProjectedPercentage is { } p ? p.ToString("0.00", CultureInfo.InvariantCulture) : null, run.ProjectedStatus.ToString()));
        }).ToList();

        var pendencies = new List<PendencyDto>();
        var review = d.Coverage.ReviewInMonth + d.Coverage.CardReviewCount;
        if (review > 0) pendencies.Add(new(MonthlyPlanCalculator.Pendencies.MovementsInReview, review));
        if (scopeAccount is null && limits.Count(l => l.ItemizedAboveLimit) is > 0 and var above) pendencies.Add(new(MonthlyPlanCalculator.Pendencies.ItemizedAboveLimit, above));
        if (totals.LimitsNotAttributable) pendencies.Add(new(MonthlyPlanCalculator.Pendencies.LimitsNotAttributable, totals.LimitCount));
        if (lines.Count(l => l.Hourly is { ExceedsCalendar: true }) is > 0 and var hourly) pendencies.Add(new(MonthlyPlanCalculator.Pendencies.HourlyIncomeAboveCalendar, hourly));
        if (lines.Count(l => !l.AccountActive && l.AccountId is not null) is > 0 and var inactive) pendencies.Add(new(MonthlyPlanCalculator.Pendencies.LineOnInactiveAccount, inactive));
        if (!totals.HasPlan) pendencies.Add(new(MonthlyPlanCalculator.Pendencies.NoPlan, 1));

        var result = MonthlyPlanCalculator.Result(totals.Income, totals.Expense);
        var planned = new PlanTotalsDto(MonthlyPlanCalculator.CentsOrNull(totals.Income), totals.IncomeLines, MonthlyPlanCalculator.CentsOrNull(totals.Expense), totals.ExpenseLines,
            MonthlyPlanCalculator.Cents(totals.Reserve), totals.ReserveLines, MonthlyPlanCalculator.CentsOrNull(result), totals.LimitCount, totals.LimitsNotAttributable, totals.TransferCount,
            MonthlyPlanCalculator.Cents(totals.TransfersOut), MonthlyPlanCalculator.Cents(totals.TransfersIn),
            totals.Income is null ? null : MonthlyPlanCalculator.Sources.MonthlyPlan, totals.Expense is null ? null : MonthlyPlanCalculator.Sources.MonthlyPlan);
        return MonthlyPlanResult<MonthlyPlanDto>.Ok(new(Key(month), d.AsOf, d.PeriodKind, d.Scope, totals.HasPlan, planned, lines, limits,
            new(d.Income, d.Expenses.Consumption, d.Cards, d.Coverage, d.SnapshotConsistent), business, pendencies));
    }

    // ---- Apoio ----------------------------------------------------------------------------------------------------------------------------

    private sealed record Lookups(IReadOnlyDictionary<Guid, FinancialAccountDto> Accounts, IReadOnlyDictionary<Guid, string> Categories);

    private async Task<Lookups> LookupsAsync(Guid workspaceId, PlanFacts facts, CancellationToken ct) => await LookupsAsync(workspaceId, facts.Lines.Select(l => l.CategoryId), ct);

    private async Task<Lookups> LookupsAsync(Guid workspaceId, IEnumerable<Guid?> categoryIds, CancellationToken ct)
    {
        var map = (await accounts.ListAsync(workspaceId, true, ct)).ToDictionary(a => a.Id);
        var names = new Dictionary<Guid, string>();
        foreach (var id in categoryIds.Where(id => id is not null).Select(id => id!.Value).Distinct())
            if (await budgetRepository.GetCategoryAsync(workspaceId, id, ct) is { } category) names[id] = category.Name;
        return new(map, names);
    }

    private static PlanLineDto ToDto(PlanLineFact l, Lookups lookups, int businessDays)
    {
        FinancialAccountDto? account = l.AccountId is { } a && lookups.Accounts.TryGetValue(a, out var found) ? found : null;
        FinancialAccountDto? destination = l.DestinationAccountId is { } t && lookups.Accounts.TryGetValue(t, out var other) ? other : null;
        return new(l.Id, Key(l.Competence), l.Kind.ToString(), l.Nature?.ToString(), l.Description, l.AccountId, account?.Name, account?.FinancialScope.ToString(), account?.IsActive ?? true,
            l.DestinationAccountId, destination?.Name, l.CategoryId, l.CategoryId is { } c && lookups.Categories.TryGetValue(c, out var name) ? name : null,
            MonthlyPlanCalculator.Cents(l.OriginalAmount), MonthlyPlanCalculator.Cents(l.Amount), MonthlyPlanCalculator.Cents(l.Amount - l.OriginalAmount), l.Amount != l.OriginalAmount,
            l.ExpectedDate, l.HourlyRate is { } rate && l.HoursPerDay is { } hours && l.BillableDays is { } days
                ? new HourlyDto(MonthlyPlanCalculator.Cents(rate), hours.ToString("0.##", CultureInfo.InvariantCulture), days, businessDays, days > businessDays) : null,
            l.UpdatedAt);
    }

    private static PlanLineFact ToFact(FinancialPlanLine l) => new(l.Id, l.Competence, l.Kind, l.Nature, l.Description, l.AccountId, l.DestinationAccountId, l.CategoryId, l.OriginalAmount,
        l.Amount, l.ExpectedDate, l.HourlyRate, l.HoursPerDay, l.BillableDays, l.UpdatedAt);

    private async Task<HashSet<DateOnly>> NonWorkingAsync(Guid workspaceId, DateOnly month, CancellationToken ct) =>
        (await repository.ListNonWorkingDaysAsync(workspaceId, month, month.AddMonths(1).AddDays(-1), ct)).Select(d => d.Date).ToHashSet();
}
