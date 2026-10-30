using Saldo360.Application.Accounts;
using Saldo360.Application.Budgets;
using Saldo360.Application.Calendar;
using Saldo360.Application.Dashboard.PlanActual;
using Saldo360.Application.Goals;
using Saldo360.Application.Pluggy;
using Saldo360.Application.Transactions;
using Saldo360.Application.Workspaces;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;
using Saldo360.Domain.Goals;

namespace Saldo360.Application.Dashboard.Overview;

// R01: blocos da Visão Geral que dependem de composição no servidor. Os quatro indicadores principais, a evolução e o orçamento continuam vindo de
// plan-actual, monthly-plan/six-months e budgets; aqui ficam as contas (saldo por conta), os compromissos da Agenda e as metas. Cada bloco tem o seu
// próprio estado: a falha de um não derruba os demais, e "indisponível" nunca vira zero. Valores monetários são texto decimal exato.

public sealed record OverviewAccountDto(
    Guid AccountId, string Name, string Scope, string Type, bool IsPrimary, bool InScope, bool BalanceAvailable, string? Balance, string? UnavailableReason);

public sealed record OverviewAccountsDto(string State, DateOnly AsOf, IReadOnlyList<OverviewAccountDto> Items);

public sealed record OverviewCommitmentDto(Guid Id, string Source, string Description, DateOnly Date, string Amount, string? AccountName, int DaysFromToday);

public sealed record OverviewCommitmentGroupDto(int Count, string Total, IReadOnlyList<OverviewCommitmentDto> Items);

/// <summary>Despesas pendentes da Agenda. <c>Overdue</c> = vencidas até ontem; <c>Upcoming</c> = de hoje até o fim do horizonte.</summary>
public sealed record OverviewCommitmentsDto(string State, DateOnly AsOf, DateOnly HorizonThrough, int HorizonDays, OverviewCommitmentGroupDto Overdue, OverviewCommitmentGroupDto Upcoming);

public sealed record OverviewGoalDto(Guid GoalId, string Name, string Priority, string Target, string Allocated, string ProgressPercent, DateOnly? TargetDate, bool Funded);

public sealed record OverviewGoalsDto(string State, int ActiveCount, int CompletedCount, IReadOnlyList<OverviewGoalDto> Items);

/// <summary>Metas por categoria do mês e o consumo de cada uma. Cada meta é lida separadamente: perspectivas diferentes (R03) nunca serão somadas num total global.</summary>
public sealed record OverviewBudgetTargetDto(Guid TargetId, Guid CategoryId, string CategoryName, string Planned, string Realized, string Committed, string Projected, string Available, string Exceeded, string? Percent, string Status);

public sealed record OverviewBudgetDto(string State, bool HasBudget, Guid? BudgetId, string? Planned, string? Projected, string? Realized, int ExceededCount, int ApproachingCount, IReadOnlyList<OverviewBudgetTargetDto> Targets);

/// <summary>Últimos lançamentos efetivos do mês selecionado (até hoje), sem pagamentos de fatura: as compras do cartão não são contadas duas vezes.</summary>
public sealed record OverviewRecentItemDto(Guid Id, string Type, string Amount, DateOnly Date, string Description, string? CategoryName, string AccountName, bool AwaitingClassification);

public sealed record OverviewRecentDto(string State, DateOnly From, DateOnly Through, int TotalCount, IReadOnlyList<OverviewRecentItemDto> Items);

/// <summary>Lançamentos do banco ainda sem categoria definida. Não tem relação com a sincronização: é uma fila de classificação.</summary>
public sealed record OverviewClassificationDto(string State, int NeedsReview, int NotEvaluated);

public sealed record DashboardOverviewDto(string Competence, DateOnly AsOf, string ScopeKind, Guid? ScopeAccountId, OverviewAccountsDto Accounts, OverviewCommitmentsDto Commitments, OverviewGoalsDto Goals,
    OverviewBudgetDto Budget, OverviewRecentDto Recent, OverviewClassificationDto Classification);

public sealed class DashboardOverviewService(
    IFinancialWorkspaceRepository workspaces,
    FinancialAccountService accountService,
    FinancialCalendarService calendar,
    FinancialGoalService goalService,
    FinancialGoalAllocationService allocationService,
    FinancialBudgetService budgetService,
    FinancialBudgetExecutionService budgetExecution,
    FinancialTransactionService transactionService,
    IPluggyCategorizationService categorization,
    TimeProvider time)
{
    public const int HorizonDays = 30;
    public const int MaxItems = 5;
    public const int MaxGoals = 4;
    public const int MaxRecent = 6;
    private const int OverdueLookbackDays = 366;

    public async Task<PlanActualResult<DashboardOverviewDto>> GetAsync(Guid workspaceId, Guid userId, string? competence, string? scope, Guid? accountId, CancellationToken ct)
    {
        if (await workspaces.GetOwnerRoleAsync(workspaceId, userId, ct) is null) return PlanActualResult<DashboardOverviewDto>.NotFound;
        var today = BusinessCalendar.Today(time.GetUtcNow());
        var month = $"{today.Year:0000}-{today.Month:00}";
        if (!string.IsNullOrWhiteSpace(competence))
        {
            if (competence.Length != 7 || competence[4] != '-' || !int.TryParse(competence[..4], out var y) || !int.TryParse(competence[5..], out var m) || y is < 2000 or > 2100 || m is < 1 or > 12)
                return Invalid("competence", "Use uma competência válida no formato yyyy-MM.");
            month = competence;
        }

        var requested = string.IsNullOrWhiteSpace(scope) ? PlanActualScopes.Primary : scope.Trim().ToLowerInvariant();
        if (requested is not (PlanActualScopes.Primary or PlanActualScopes.All or PlanActualScopes.Account)) return Invalid("scope", "Use primary, all ou account.");

        var all = await accountService.ListAsync(workspaceId, userId, includeInactive: false, ct);
        if (all is null) return PlanActualResult<DashboardOverviewDto>.NotFound;
        FinancialAccountDto? selected = null;
        if (requested == PlanActualScopes.Account)
        {
            if (accountId is null) return Invalid("accountId", "Informe a conta.");
            // A conta pode estar inativa: ainda é uma escolha válida de escopo (o plano e o realizado dela continuam consultáveis).
            selected = all.FirstOrDefault(a => a.Id == accountId) ?? (await accountService.ListAsync(workspaceId, userId, includeInactive: true, ct))?.FirstOrDefault(a => a.Id == accountId);
            if (selected is null) return PlanActualResult<DashboardOverviewDto>.NotFound;
        }
        else if (requested == PlanActualScopes.Primary) selected = all.FirstOrDefault(a => a.IsPrimary);
        var kind = selected is null ? PlanActualScopes.All : requested == PlanActualScopes.Primary ? PlanActualScopes.Primary : PlanActualScopes.Account;

        var accounts = await BuildAccountsAsync(workspaceId, userId, all, selected, today, ct);
        var commitments = await BuildCommitmentsAsync(workspaceId, userId, selected?.Id, today, ct);
        var goals = await BuildGoalsAsync(workspaceId, userId, ct);
        var budget = await BuildBudgetAsync(workspaceId, userId, month, ct);
        var recent = await BuildRecentAsync(workspaceId, userId, month, selected?.Id, today, ct);
        var classification = await BuildClassificationAsync(workspaceId, ct);
        return PlanActualResult<DashboardOverviewDto>.Ok(new(month, today, kind, selected?.Id, accounts, commitments, goals, budget, recent, classification));
    }

    private static PlanActualResult<DashboardOverviewDto> Invalid(string field, string message) =>
        new(PlanActualAccess.Invalid, null, new Dictionary<string, string[]> { [field] = [message] });

    private async Task<OverviewAccountsDto> BuildAccountsAsync(Guid workspaceId, Guid userId, IReadOnlyList<FinancialAccountDto> all, FinancialAccountDto? selected, DateOnly today, CancellationToken ct)
    {
        var items = new List<OverviewAccountDto>();
        var failed = false;
        foreach (var account in all.OrderByDescending(a => a.IsPrimary).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            string? balance = null, reason = null;
            try
            {
                var result = await accountService.GetBalanceAsync(workspaceId, account.Id, userId, null, ct);
                if (result.Access == BalanceAccess.Allowed && result.Balance is { } dto)
                {
                    if (dto.Balance is { } value) balance = Money.Format(value);
                    else reason = dto.UnavailableReason?.ToString() ?? "Unavailable";
                }
                else reason = "Unavailable";
            }
            catch (Exception) when (!ct.IsCancellationRequested) { reason = "Unavailable"; failed = true; }
            items.Add(new(account.Id, account.Name, account.FinancialScope.ToString(), account.Type.ToString(), account.IsPrimary, selected is null || selected.Id == account.Id, balance is not null, balance, reason));
        }
        return new(failed ? "Partial" : "Complete", today, items);
    }

    private async Task<OverviewCommitmentsDto> BuildCommitmentsAsync(Guid workspaceId, Guid userId, Guid? accountId, DateOnly today, CancellationToken ct)
    {
        var through = today.AddDays(HorizonDays);
        var empty = new OverviewCommitmentGroupDto(0, Money.Format(0m), []);
        try
        {
            var result = await calendar.ListAsync(workspaceId, userId, new(today.AddDays(-OverdueLookbackDays), through, accountId, "Expense"), ct);
            if (result.Calendar is null) return new("Unavailable", today, through, HorizonDays, empty, empty);
            var pending = result.Calendar.Items.Where(i => i.Type == CategoryType.Expense && i.Status is FinancialCalendarStatus.Pending or FinancialCalendarStatus.Overdue).ToList();
            OverviewCommitmentGroupDto Group(IEnumerable<FinancialCalendarItemDto> source)
            {
                var list = source.OrderBy(i => i.Date).ThenBy(i => i.Description, StringComparer.CurrentCultureIgnoreCase).ThenBy(i => i.Id).ToList();
                return new(list.Count, Money.Format(list.Sum(i => i.Amount)),
                    list.Take(MaxItems).Select(i => new OverviewCommitmentDto(i.Id, i.Source.ToString(), i.Description, i.Date, Money.Format(i.Amount), i.Account?.Name, i.Date.DayNumber - today.DayNumber)).ToList());
            }
            return new("Complete", today, through, HorizonDays, Group(pending.Where(i => i.Date < today)), Group(pending.Where(i => i.Date >= today)));
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return new("Unavailable", today, through, HorizonDays, empty, empty);
        }
    }

    private async Task<OverviewGoalsDto> BuildGoalsAsync(Guid workspaceId, Guid userId, CancellationToken ct)
    {
        try
        {
            var (items, notFound) = await goalService.ListAsync(workspaceId, userId, includeArchived: false, ct);
            if (notFound || items is null) return new("Unavailable", 0, 0, []);
            var active = items.Where(g => g.Status == FinancialGoalStatus.Active).ToList();
            var visible = new List<OverviewGoalDto>();
            foreach (var goal in active.OrderByDescending(g => g.Priority).ThenBy(g => g.TargetDate ?? DateOnly.MaxValue).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).Take(MaxGoals))
            {
                var progress = await allocationService.ProgressAsync(workspaceId, goal.Id, userId, ct);
                if (progress is null) continue;
                visible.Add(new(goal.Id, goal.Name, goal.Priority.ToString(), Money.Format(progress.TargetAmount), Money.Format(progress.AllocatedAmount),
                    progress.ProgressPercentage.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), goal.TargetDate, progress.IsFunded));
            }
            return new("Complete", active.Count, items.Count(g => g.Status == FinancialGoalStatus.Completed), visible);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return new("Unavailable", 0, 0, []);
        }
    }

    private async Task<OverviewBudgetDto> BuildBudgetAsync(Guid workspaceId, Guid userId, string month, CancellationToken ct)
    {
        try
        {
            var list = await budgetService.ListAsync(workspaceId, userId, month, includeArchived: false, ct);
            if (list is null) return new("Unavailable", false, null, null, null, null, 0, 0, []);
            var budget = list.FirstOrDefault(b => b.Targets.Any(t => !t.IsArchived)) ?? list.FirstOrDefault();
            if (budget is null || budget.Targets.All(t => t.IsArchived)) return new("Complete", false, budget?.Id, null, null, null, 0, 0, []);
            var execution = await budgetExecution.GetAsync(workspaceId, budget.Id, userId, ct);
            if (execution is null) return new("Unavailable", true, budget.Id, null, null, null, 0, 0, []);
            var names = budget.Targets.Where(t => !t.IsArchived).ToDictionary(t => t.Id, t => t.Category.Path);
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            var targets = execution.Targets.Where(t => names.ContainsKey(t.TargetId))
                .Select(t => new OverviewBudgetTargetDto(t.TargetId, t.CategoryId, names[t.TargetId], Money.Format(t.Amounts.Planned), Money.Format(t.Amounts.Realized), Money.Format(t.Amounts.Committed),
                    Money.Format(t.Amounts.ProjectedConsumption), Money.Format(t.Amounts.Available), Money.Format(t.Amounts.ExceededAmount),
                    t.Amounts.ConsumptionPercentage is { } p ? p.ToString("0.0", invariant) : null, t.Status.ToString()))
                .OrderByDescending(t => SeverityRank(t.Status)).ThenByDescending(t => t.Percent is not null && decimal.TryParse(t.Percent, System.Globalization.NumberStyles.Number, invariant, out var v) ? v : -1m)
                .ThenBy(t => t.CategoryName, StringComparer.CurrentCultureIgnoreCase).ToList();
            return new("Complete", true, budget.Id, Money.Format(execution.Total.Planned), Money.Format(execution.Total.ProjectedConsumption), Money.Format(execution.Total.Realized),
                execution.Total.ExceededCount, execution.Total.ApproachingCount + execution.Total.ReachedCount, targets);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return new("Unavailable", false, null, null, null, null, 0, 0, []);
        }
    }

    private static int SeverityRank(string status) => status switch { "Exceeded" => 3, "LimitReached" => 2, "ApproachingLimit" => 1, _ => 0 };

    private async Task<OverviewRecentDto> BuildRecentAsync(Guid workspaceId, Guid userId, string month, Guid? accountId, DateOnly today, CancellationToken ct)
    {
        var first = new DateOnly(int.Parse(month[..4]), int.Parse(month[5..]), 1);
        var through = first.AddMonths(1).AddDays(-1);
        if (through > today) through = today;
        if (first > through) return new("Complete", first, first, 0, []); // mês futuro: ainda não há lançamentos efetivos
        try
        {
            var result = await transactionService.ListAsync(workspaceId, userId, new(first, through, accountId, null, null, false, 1, MaxRecent, ExcludeInvoicePayments: true), ct);
            if (result.Page is null) return new("Unavailable", first, through, 0, []);
            var items = result.Page.Items.Select(t => new OverviewRecentItemDto(t.Id, t.Type.ToString(), Money.Format(t.Amount), t.TransactionDate, t.Description, t.Category?.Path, t.Account.Name,
                t.Category is null || t.CategoryNeedsReview)).ToList();
            return new("Complete", first, through, result.Page.TotalCount, items);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return new("Unavailable", first, through, 0, []);
        }
    }

    private async Task<OverviewClassificationDto> BuildClassificationAsync(Guid workspaceId, CancellationToken ct)
    {
        try
        {
            var counts = (await categorization.GetOverviewAsync(workspaceId, ct)).Counts;
            return new("Complete", counts.NeedsReview, counts.NotEvaluated);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return new("Unavailable", 0, 0);
        }
    }
}
