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
/// <summary>
/// Próximo pagamento de uma fatura: obrigação de CAIXA pelo vencimento (o que ainda falta pagar), não as compras da fatura. <c>Status</c>: Confirmed (total do banco),
/// Provisional (projeção sem fatura fechada) ou AwaitingBankBill (fatura do banco ainda não chegou).
/// </summary>
public sealed record OverviewCardPaymentDto(Guid CardId, string CardName, string? CardScope, string Competence, DateOnly Due, int DaysFromToday, string Obligation, string Paid, string Remaining, string Status, string PaymentState);

/// <summary><c>Total</c> soma só o que falta pagar das próximas faturas listadas; <c>OverdueRemaining</c> é o que falta de faturas já vencidas (por cartão não listado a data).</summary>
public sealed record OverviewCardPaymentsDto(string State, string Total, string OverdueRemaining, int OverdueCards, IReadOnlyList<OverviewCardPaymentDto> Items);

public sealed record OverviewCommitmentsDto(string State, DateOnly AsOf, DateOnly HorizonThrough, int HorizonDays, OverviewCommitmentGroupDto Overdue, OverviewCommitmentGroupDto Upcoming, OverviewCardPaymentsDto? CardPayments = null);

public sealed record OverviewGoalDto(Guid GoalId, string Name, string Priority, string Target, string Allocated, string Remaining, string ProgressPercent, DateOnly? TargetDate, bool Funded);

public sealed record OverviewGoalsDto(string State, int ActiveCount, int CompletedCount, IReadOnlyList<OverviewGoalDto> Items);

/// <summary>
/// Uma meta do mês e o que ela consumiu (R03). <c>Group</c> = Category, FixedExpenses ou AllCards. Cada meta é lida separadamente: as perspectivas
/// contam os mesmos gastos e nunca são somadas num total global. <c>Reasons</c> explica dados incompletos (EstimatedCompetence, UndefinedNature, NoCards).
/// </summary>
public sealed record OverviewBudgetTargetDto(Guid TargetId, string Group, Guid? CategoryId, string Name, string Planned, string Consumed, string Pending, string Projected, string Available,
    string AvailableProjected, string Exceeded, string? Percent, string Status, string ProjectedStatus, string State, IReadOnlyList<string> Reasons);

/// <summary>Resumo das metas de categoria (que não se sobrepõem, então a soma é válida); fixas e cartões ficam à parte.</summary>
public sealed record OverviewBudgetCategoriesDto(int Count, string? Planned, string? Consumed, int ExceededCount, int ApproachingCount, IReadOnlyList<OverviewBudgetTargetDto> Targets);

public sealed record OverviewBudgetDto(string State, bool HasBudget, Guid? BudgetId, string Competence, OverviewBudgetTargetDto? Fixed, OverviewBudgetTargetDto? Cards, OverviewBudgetCategoriesDto Categories);

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
    Saldo360.Application.CreditCards.Projection.CardProjectionService cardProjection,
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
        commitments = commitments with { CardPayments = await BuildCardPaymentsAsync(workspaceId, userId, selected?.Id, today, ct) };
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

    /// <summary>
    /// Próxima fatura a pagar de cada cartão ativo, pela mesma projeção de Cartões e da Agenda (nada é recalculado aqui). Com uma conta escolhida, só os cartões que ela
    /// paga. A obrigação é a fatura pelo vencimento; as compras individuais continuam sendo consumo do orçamento e não entram como saídas bancárias.
    /// </summary>
    private async Task<OverviewCardPaymentsDto> BuildCardPaymentsAsync(Guid workspaceId, Guid userId, Guid? accountId, DateOnly today, CancellationToken ct)
    {
        var empty = new OverviewCardPaymentsDto("Unavailable", Money.Format(0m), Money.Format(0m), 0, []);
        try
        {
            var result = await cardProjection.InvoiceOverviewAsync(workspaceId, userId, null, ct);
            if (result.Value is null) return empty;
            var cards = result.Value.Cards.Where(card => accountId is null || card.Payer?.AccountId == accountId).ToList();
            var items = cards.Where(card => card.NextDue is not null).Select(card =>
            {
                var invoice = card.NextDue!;
                var status = invoice.AwaitingBankBill ? "AwaitingBankBill" : invoice.Provisional ? "Provisional" : "Confirmed";
                return new OverviewCardPaymentDto(card.CardId, card.Name, card.Scope, invoice.Competence, invoice.Due, invoice.Due.DayNumber - today.DayNumber, invoice.Obligation, invoice.Paid, invoice.Remaining, status, invoice.PaymentState);
            }).OrderBy(item => item.Due).ThenBy(item => item.CardName, StringComparer.CurrentCultureIgnoreCase).ToList();
            var parse = (string value) => decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            var overdue = cards.Select(card => parse(card.OverdueRemaining)).ToList();
            return new("Complete", Money.Format(items.Sum(item => parse(item.Remaining))), Money.Format(overdue.Sum()), overdue.Count(value => value > 0m), items);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return empty;
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
                visible.Add(new(goal.Id, goal.Name, goal.Priority.ToString(), Money.Format(progress.TargetAmount), Money.Format(progress.AllocatedAmount), Money.Format(progress.RemainingToTarget),
                    progress.ProgressPercentage.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), goal.TargetDate, progress.IsFunded));
            }
            return new("Complete", active.Count, items.Count(g => g.Status == FinancialGoalStatus.Completed), visible);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return new("Unavailable", 0, 0, []);
        }
    }

    private static OverviewBudgetCategoriesDto NoCategories() => new(0, null, null, 0, 0, []);

    private async Task<OverviewBudgetDto> BuildBudgetAsync(Guid workspaceId, Guid userId, string month, CancellationToken ct)
    {
        try
        {
            var list = await budgetService.ListAsync(workspaceId, userId, month, includeArchived: false, ct);
            if (list is null) return new("Unavailable", false, null, month, null, null, NoCategories());
            var budget = list.FirstOrDefault(b => b.Targets.Any(t => !t.IsArchived)) ?? list.FirstOrDefault();
            if (budget is null || budget.Targets.All(t => t.IsArchived)) return new("Complete", false, budget?.Id, month, null, null, NoCategories());
            var execution = await budgetExecution.GetAsync(workspaceId, budget.Id, userId, ct);
            if (execution is null) return new("Unavailable", true, budget.Id, month, null, null, NoCategories());
            var active = budget.Targets.Where(t => !t.IsArchived).ToDictionary(t => t.Id);
            var targets = execution.Targets.Where(t => active.ContainsKey(t.TargetId)).Select(t => ToOverviewTarget(active[t.TargetId], t)).ToList();
            var categories = targets.Where(t => t.Group == nameof(Saldo360.Domain.Budgets.BudgetTargetGroupingType.Category))
                .OrderByDescending(t => SeverityRank(t.Status)).ThenByDescending(t => t.Percent is not null && decimal.TryParse(t.Percent, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : -1m)
                .ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            var summary = execution.Categories;
            return new("Complete", true, budget.Id, month,
                targets.FirstOrDefault(t => t.Group == nameof(Saldo360.Domain.Budgets.BudgetTargetGroupingType.FixedExpenses)),
                targets.FirstOrDefault(t => t.Group == nameof(Saldo360.Domain.Budgets.BudgetTargetGroupingType.AllCards)),
                new(categories.Count, categories.Count == 0 ? null : Money.Format(summary.Planned), categories.Count == 0 ? null : Money.Format(summary.Consumed),
                    summary.ExceededCount, summary.ApproachingCount + summary.ReachedCount, categories));
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return new("Unavailable", false, null, month, null, null, NoCategories());
        }
    }

    private static OverviewBudgetTargetDto ToOverviewTarget(FinancialBudgetTargetDto target, FinancialBudgetTargetExecutionDto run)
    {
        var amounts = run.Amounts;
        var reasons = new List<string>();
        if (run.Coverage is { } coverage)
        {
            if (coverage.EstimatedCompetenceCount > 0) reasons.Add("EstimatedCompetence");
            if (coverage.UndefinedNatureCount > 0) reasons.Add("UndefinedNature");
            if (coverage.NoCards) reasons.Add("NoCards");
        }
        var state = reasons.Contains("NoCards") ? "Unavailable" : reasons.Count > 0 ? "Partial" : "Complete";
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        return new(target.Id, target.GroupingType.ToString(), target.Category?.Id, target.Category?.Path ?? target.Name, Money.Format(amounts.Planned), Money.Format(amounts.Consumed),
            Money.Format(amounts.Committed), Money.Format(amounts.ProjectedConsumption), Money.Format(amounts.Available), Money.Format(amounts.AvailableProjected),
            Money.Format(amounts.ExceededAmount), amounts.ConsumptionPercentage is { } p ? p.ToString("0.0", invariant) : null, run.Status.ToString(), run.ProjectedStatus.ToString(), state, reasons);
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
