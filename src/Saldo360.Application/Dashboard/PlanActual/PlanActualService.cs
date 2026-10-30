using System.Globalization;
using Saldo360.Application.Accounts;
using Saldo360.Application.Accounts.Reconciliation;
using Saldo360.Application.Calendar;
using Saldo360.Application.CreditCards.Projection;
using Saldo360.Application.MonthlyPlan;
using Saldo360.Application.Pluggy;
using Saldo360.Application.Workspaces;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Common;
using Saldo360.Domain.Budgets;

namespace Saldo360.Application.Dashboard.PlanActual;

/// <summary>Previsões (planejado original) do período: valor e quantidade por tipo. Quantidade zero significa "não definido", nunca um zero fictício.</summary>
public sealed record PlannedFacts(decimal Income, int IncomeCount, decimal Expense, int ExpenseCount);
/// <summary>Movimentações PENDING da Pluggy (ainda não efetivadas): não são realizado; entram só como compromisso conhecido de despesa.</summary>
public sealed record PendingBankFacts(decimal Income, decimal Expense);
/// <summary>Movimentações em revisão por conta (todas e as do mês) e as dos cartões por conta pagadora.</summary>
public sealed record ReviewFacts(IReadOnlyDictionary<Guid, int> Total, IReadOnlyDictionary<Guid, int> InMonth, IReadOnlyDictionary<Guid, int> CardByPayer);
public sealed record CardTargetRow(Guid PayerAccountId, DateOnly EffectiveFrom, decimal? Amount);
/// <summary>Conexão Pluggy que alimenta uma conta bancária ou os cartões pagos por uma conta.</summary>
public sealed record ConnectionLink(Guid ConnectionId, Guid? AccountId, Guid? CardPayerAccountId);

/// <summary>Fatos de uma competência que alimentam o planejado e o projetado de despesas: previsões, movimentações PENDING da Pluggy e metas agregadas.</summary>
public sealed record PlanBasisFacts(PlannedFacts Planned, PendingBankFacts PendingBank, decimal? AggregateCardTarget, decimal? AggregateTotalExpenses);

public sealed record PlanActualFacts(
    PlannedFacts Planned, PendingBankFacts PendingBank, IReadOnlyList<CardTargetRow> CardTargets, decimal? AggregateCardTarget, decimal? AggregateTotalExpenses,
    ReviewFacts Review, IReadOnlyList<ConnectionLink> Connections, IReadOnlySet<Guid> AccountsWithActivity);

/// <summary>Impressão digital barata dos dados que alimentam o painel: se mudar durante a montagem, a montagem é refeita.</summary>
public sealed record DataFingerprint(long Transactions, decimal TransactionTotal, long Planned, decimal PlannedTotal, long Imported, long Purchases, long Anchors, DateTimeOffset? LastRun, long PlanLines = 0, decimal PlanTotal = 0m, DateTimeOffset? PlanLastChange = null);

public enum CardTargetSaveOutcome { Saved, AccountNotFound, AccountNotEligible }

public interface IPlanActualRepository
{
    /// <summary>Fatos próprios do painel, restritos ao orçamento. <paramref name="scopeAccountIds"/> nulo = todas as contas. Somente leitura.</summary>
    Task<PlanActualFacts> LoadAsync(Guid workspaceId, DateOnly month, IReadOnlyList<Guid>? scopeAccountIds, CancellationToken cancellationToken);
    /// <summary>Somente a base de planejado/projetado de uma competência (sem revisões nem conexões): usada pela visão de seis meses. Mesmas regras do <see cref="LoadAsync"/>.</summary>
    Task<PlanBasisFacts> LoadBasisAsync(Guid workspaceId, DateOnly month, IReadOnlyList<Guid>? scopeAccountIds, CancellationToken cancellationToken);
    Task<DataFingerprint> FingerprintAsync(Guid workspaceId, CancellationToken cancellationToken);
    /// <summary>Grava a meta de cartões de uma conta pagadora a partir da competência (substitui a linha da mesma vigência, se houver). Nunca altera outras vigências.</summary>
    Task SaveCardTargetAsync(Guid workspaceId, Guid userId, Guid accountId, DateOnly effectiveFrom, decimal? amount, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>
/// Dashboard Planejado × Projetado × Realizado (T10.1). Orquestra os serviços existentes (projeção mensal e saldos da Agenda, projeção de cartões,
/// conciliação de saldo e monitoramento da sincronização) e só compara os números que eles devolvem: não há um segundo motor de cálculos. Somente leitura.
/// </summary>
public sealed class PlanActualService(
    IFinancialWorkspaceRepository workspaces, IFinancialAccountRepository accounts, FinancialCalendarMonthlyProjectionService monthlyProjection,
    FinancialCalendarBalanceService calendar, CardProjectionService cardProjection, AccountBalanceReconciliationService reconciliation,
    IAccountBalanceReconciliationRepository reconciliationFacts, IFinancialCalendarBalanceRepository calendarSource, PluggySyncMonitorService syncMonitor, IMonthlyPlanRepository monthlyPlan, IPlanActualRepository repository, TimeProvider time)
{
    private const int MaxAttempts = 3;

    public async Task<PlanActualResult<PlanActualDashboardDto>> GetAsync(Guid workspaceId, Guid userId, string? competence, string? scope, Guid? accountId, CancellationToken ct)
    {
        if (await workspaces.GetOwnerRoleAsync(workspaceId, userId, ct) is null) return PlanActualResult<PlanActualDashboardDto>.NotFound;
        var today = BusinessCalendar.Today(time.GetUtcNow());
        var month = new DateOnly(today.Year, today.Month, 1);
        if (!string.IsNullOrWhiteSpace(competence) && !TryMonth(competence, out month)) return PlanActualResult<PlanActualDashboardDto>.Invalid("competence", "Use uma competência válida no formato yyyy-MM.");
        var requested = string.IsNullOrWhiteSpace(scope) ? PlanActualScopes.Primary : scope.Trim().ToLowerInvariant();
        if (requested is not (PlanActualScopes.Primary or PlanActualScopes.All or PlanActualScopes.Account)) return PlanActualResult<PlanActualDashboardDto>.Invalid("scope", "Use primary, all ou account.");

        var all = await accounts.ListAsync(workspaceId, true, ct);
        var primary = all.FirstOrDefault(a => a.IsPrimary);
        FinancialAccountDto? selected = null;
        if (requested == PlanActualScopes.Account)
        {
            if (accountId is null) return PlanActualResult<PlanActualDashboardDto>.Invalid("accountId", "Informe a conta.");
            selected = all.FirstOrDefault(a => a.Id == accountId);
            if (selected is null) return PlanActualResult<PlanActualDashboardDto>.NotFound;
        }
        else if (requested == PlanActualScopes.Primary) selected = primary;
        var kind = selected is null ? PlanActualScopes.All : requested == PlanActualScopes.Primary ? PlanActualScopes.Primary : PlanActualScopes.Account;

        PlanActualDashboardDto? last = null;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var before = await repository.FingerprintAsync(workspaceId, ct);
            var built = await BuildAsync(workspaceId, userId, month, today, requested, kind, selected, primary, all, ct);
            if (built is null) return PlanActualResult<PlanActualDashboardDto>.NotFound;
            last = built;
            if (before == await repository.FingerprintAsync(workspaceId, ct)) return PlanActualResult<PlanActualDashboardDto>.Ok(last with { SnapshotConsistent = true });
        }
        return PlanActualResult<PlanActualDashboardDto>.Ok(last! with { SnapshotConsistent = false });
    }

    /// <summary>Define (ou encerra, com valor nulo) a meta mensal de cartões da conta pagadora a partir da competência. Não toca nas vigências anteriores.</summary>
    public async Task<PlanActualResult<CardTargetSavedDto>> SaveCardTargetAsync(Guid workspaceId, Guid userId, CardTargetRequest request, CancellationToken ct)
    {
        if (await workspaces.GetOwnerRoleAsync(workspaceId, userId, ct) is null) return PlanActualResult<CardTargetSavedDto>.NotFound;
        if (request.AccountId is not { } accountId) return PlanActualResult<CardTargetSavedDto>.Invalid("accountId", "Informe a conta pagadora.");
        if (string.IsNullOrWhiteSpace(request.Competence) || !TryMonth(request.Competence, out var from)) return PlanActualResult<CardTargetSavedDto>.Invalid("competence", "Use uma competência válida no formato yyyy-MM.");
        decimal? amount = null;
        if (!string.IsNullOrWhiteSpace(request.Amount))
        {
            if (!decimal.TryParse(request.Amount, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed) || parsed < 0m)
                return PlanActualResult<CardTargetSavedDto>.Invalid("amount", "Informe um valor decimal maior ou igual a zero, com ponto.");
            if (Money.Validate(parsed, "A meta") is { } error) return PlanActualResult<CardTargetSavedDto>.Invalid("amount", error);
            amount = parsed;
        }
        var account = (await accounts.ListAsync(workspaceId, true, ct)).FirstOrDefault(a => a.Id == accountId);
        if (account is null) return PlanActualResult<CardTargetSavedDto>.NotFound;
        if (!account.IsActive || !FinancialAccount.IsBankType(account.Type)) return PlanActualResult<CardTargetSavedDto>.Invalid("accountId", "A meta pertence a uma conta bancária ativa (a que paga os cartões).");
        await repository.SaveCardTargetAsync(workspaceId, userId, accountId, from, amount, time.GetUtcNow(), ct);
        return PlanActualResult<CardTargetSavedDto>.Ok(new(accountId, from.ToString("yyyy-MM", CultureInfo.InvariantCulture), amount is { } a ? PlanActualCalculator.Cents(a) : null));
    }

    private async Task<PlanActualDashboardDto?> BuildAsync(
        Guid workspaceId, Guid userId, DateOnly month, DateOnly today, string requested, string kind, FinancialAccountDto? selected, FinancialAccountDto? primary,
        IReadOnlyList<FinancialAccountDto> all, CancellationToken ct)
    {
        var monthEnd = month.AddMonths(1).AddDays(-1);
        var competence = month.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var periodKind = PlanActualCalculator.PeriodKind(month, today);
        var scopeIds = selected is null ? null : (IReadOnlyList<Guid>)[selected.Id];
        var inScope = selected is null ? all : [selected];

        var projection = await monthlyProjection.GetAsync(workspaceId, userId, new FinancialCalendarMonthlyProjectionQuery(month, 1, selected?.Id), ct, today);
        if (projection.NotFound || projection.Projection is null) return null;
        var m = projection.Projection.Months[0];
        var balances = await calendar.GetAsync(workspaceId, userId, new FinancialCalendarBalanceQuery(month, monthEnd, selected?.Id), ct);
        if (balances.NotFound) return null;

        // Saldo inicial efetivo de cada conta (manual ou derivado da referência bancária). Se o mês começa antes dele, a abertura do mês não existe, mas o fechamento
        // continua calculável a partir da data do saldo inicial: usamos essa parte e dizemos a limitação, em vez de esconder o fechamento.
        var openings = new Dictionary<Guid, AccountOpeningBalance?>();
        foreach (var account in inScope) openings[account.Id] = await accounts.GetOpeningBalanceAsync(workspaceId, account.Id, ct);
        var window = new MonthWindow(m.ClosingBalance, m.MinimumProjectedBalance, m.FirstNegativeProjectedDate, balances.Balances, null);
        if (m.UnavailableReason == BalanceUnavailableReason.BeforeOpeningBalanceDate)
        {
            var from = openings.Values.Where(o => o?.Date is not null).Select(o => o!.Date!.Value).DefaultIfEmpty().Max();
            if (from > month && from <= monthEnd && (await calendar.GetAsync(workspaceId, userId, new FinancialCalendarBalanceQuery(from, monthEnd, selected?.Id), ct)).Balances is { UnavailableReason: null } partial)
                window = new(partial.Days[^1].ClosingBalance, partial.Risk.MinimumProjectedBalance, partial.Risk.FirstNegativeProjectedDate, partial, from);
        }

        var facts = await repository.LoadAsync(workspaceId, month, scopeIds, ct);
        // T10.2: o planejamento mensal (receitas, despesas e limites por categoria) é a fonte do planejado quando existe; as previsões e metas anteriores são o reserva.
        var plan = MonthlyPlanCalculator.Planned(await monthlyPlan.LoadAsync(workspaceId, month, month, ct), selected?.Id);
        var planAccounts = await monthlyPlan.AccountsWithPlanAsync(workspaceId, month, month, ct);
        var overview = (await reconciliation.BuildOverviewAsync(workspaceId, ct)).Accounts.ToDictionary(a => a.AccountId);
        var history = (await reconciliationFacts.LoadAsync(workspaceId, null, ct)).ToDictionary(f => f.AccountId);
        var cards = (await cardProjection.ComputeWithBillsAsync(workspaceId, today, ct))
            .Where(c => selected is null || c.Projection.Card.PayerAccountId == selected.Id).ToList();

        // ---- Cobertura e revisão ------------------------------------------------------------------------------------------
        var coverageAccounts = new List<CoverageAccountDto>();
        foreach (var account in inScope)
        {
            if (!overview.TryGetValue(account.Id, out var recon)) continue;
            history.TryGetValue(account.Id, out var accountFacts);
            var historyStart = accountFacts?.ImportFrom ?? recon.Coverage.ImportedFrom;
            var partial = month <= today && (historyStart is null || historyStart > month);
            coverageAccounts.Add(new(account.Id, account.Name, recon.Status, recon.Review.Count, facts.Review.InMonth.GetValueOrDefault(account.Id), recon.Bank?.Reliability, recon.Calculated.Amount is not null, partial));
        }
        var reviewTotal = coverageAccounts.Sum(a => a.ReviewCount);
        var reviewInMonth = coverageAccounts.Sum(a => a.ReviewInMonth);
        var cardReview = selected is null ? facts.Review.CardByPayer.Values.Sum() : facts.Review.CardByPayer.GetValueOrDefault(selected.Id);
        var partialPeriod = coverageAccounts.Any(a => a.PartialPeriod);
        var affects = reviewInMonth > 0 || (reviewTotal > 0 && m.UnavailableReason is not null);
        var coverageReasons = new List<string>();
        if (reviewInMonth > 0 || cardReview > 0) coverageReasons.Add(PlanActualCalculator.Reasons.MovementsInReview);
        if (partialPeriod) coverageReasons.Add(PlanActualCalculator.Reasons.PartialPeriod);
        var cardsIncomplete = !m.CreditCardProjectionComplete || cards.Any(c => !c.Projection.Coverage.Complete);
        if (cardsIncomplete) coverageReasons.Add(PlanActualCalculator.Reasons.CardCoverageIncomplete);
        if (!m.DebtProjectionComplete) coverageReasons.Add(PlanActualCalculator.Reasons.DebtCoverageIncomplete);
        var caveats = coverageReasons.Where(r => r is PlanActualCalculator.Reasons.MovementsInReview or PlanActualCalculator.Reasons.PartialPeriod).ToList();
        var coverage = new CoverageDto(coverageReasons.Count == 0, reviewTotal, reviewInMonth, cardReview, affects, partialPeriod, coverageAccounts, coverageReasons);

        // ---- Receitas -------------------------------------------------------------------------------------------------------
        var incomeReasons = new List<string>(caveats);
        var (plannedIncome, incomeSource) = MonthlyPlanCalculator.PlannedIncome(plan, facts.Planned);
        if (plannedIncome is null) incomeReasons.Add(PlanActualCalculator.Reasons.PlanNotDefined);
        var income = PlanActualCalculator.Indicator(plannedIncome, incomeSource, m.RealizedIncome, m.PendingIncome,
            [new("PendingPlanned", PlanActualCalculator.Cents(m.PendingIncome))], incomeReasons);

        // ---- Despesas (consumo e fluxo de caixa) ----------------------------------------------------------------------------
        var slices = cards.Select(c => PlanActualCalculator.Slice(c.Projection, c.Bills, competence)).ToList();
        var basis = PlanActualCalculator.Consumption(m.RealizedExpense, m.CardPayments, m.PendingExpense, m.CreditCardOutstanding, slices, facts.PendingBank.Expense);
        var bankExpenses = basis.BankExpenses;
        var cardOutstanding = basis.CardOutstanding;
        var pendingNonCard = basis.PendingNonCard;
        var expenseReasons = new List<string>(caveats);
        if (cardsIncomplete) expenseReasons.Add(PlanActualCalculator.Reasons.CardCoverageIncomplete);
        if (!m.DebtProjectionComplete) expenseReasons.Add(PlanActualCalculator.Reasons.DebtCoverageIncomplete);

        var cashReasons = new List<string>(expenseReasons);
        decimal? plannedCash = facts.Planned.ExpenseCount > 0 ? facts.Planned.Expense : null;
        if (plannedCash is null) cashReasons.Add(PlanActualCalculator.Reasons.PlanNotDefined);
        var cash = PlanActualCalculator.Indicator(plannedCash, "PlannedTransactions", m.RealizedExpense, m.PendingExpense + facts.PendingBank.Expense,
            [new("BankExpenses", PlanActualCalculator.Cents(bankExpenses)), new("CardInvoicePayments", PlanActualCalculator.Cents(m.CardPayments)), new("PendingPlanned", PlanActualCalculator.Cents(pendingNonCard)),
             new("PendingCardInvoices", PlanActualCalculator.Cents(cardOutstanding)), new("PendingBank", PlanActualCalculator.Cents(facts.PendingBank.Expense))], cashReasons);

        var consumptionReasons = new List<string>(expenseReasons);
        var (plannedConsumption, consumptionSource) = MonthlyPlanCalculator.PlannedConsumption(plan, m.BudgetTarget, facts.AggregateTotalExpenses, selected is null);
        // Numa conta específica, sem linhas de despesa dela, o planejado por categoria (consolidado) não é atribuível a ela.
        if (plannedConsumption is null) consumptionReasons.Add(selected is null ? PlanActualCalculator.Reasons.PlanNotDefined : PlanActualCalculator.Reasons.PlanNotAttributable);
        var cardConfirmed = basis.CardConfirmed;
        var cardPending = basis.CardPendingKnown + basis.CardEstimated;
        var consumption = PlanActualCalculator.Indicator(plannedConsumption, consumptionSource, basis.Realized, basis.Pending,
            [new("BankExpenses", PlanActualCalculator.Cents(bankExpenses)), new("CardPurchases", PlanActualCalculator.Cents(cardConfirmed)), new("PendingPlanned", PlanActualCalculator.Cents(pendingNonCard)),
             new("PendingBank", PlanActualCalculator.Cents(facts.PendingBank.Expense)), new("PendingCardPurchases", PlanActualCalculator.Cents(slices.Sum(s => s.PendingKnown))),
             new("EstimatedCardInstallments", PlanActualCalculator.Cents(slices.Sum(s => s.Estimated)))], consumptionReasons);
        var expenses = new ExpensesDto("Consumption", consumption, cash);

        // ---- Meta dos cartões ---------------------------------------------------------------------------------------------
        var cardsDto = BuildCardTarget(selected, slices, cards.Select(c => c.Projection).ToList(), facts, month, m.CreditCardProjectionComplete);

        // ---- Cheque especial --------------------------------------------------------------------------------------------
        var overdraftResults = new List<OverdraftResult>();
        foreach (var account in inScope)
        {
            if (!history.TryGetValue(account.Id, out var accountFacts)) continue;
            var eligible = (accountFacts.OverdraftContractedLimit ?? 0m) > 0m || (accountFacts.OverdraftUsedLimit ?? 0m) > 0m;
            if (!eligible) continue;
            var current = accountFacts.Anchors.FirstOrDefault(a => a.IsCurrent && a.Kind == nameof(BalanceAnchorKind.CurrentBalance));
            var observations = accountFacts.Anchors.Where(a => a.Kind == nameof(BalanceAnchorKind.CurrentBalance) && a.Reliability != nameof(BalanceAnchorReliability.Unreliable))
                .Select(a => new OverdraftObservation(a.AsOfDate, a.Amount, a.ObservedAt)).ToList();
            decimal? closing = null;
            if (periodKind != "Past")
            {
                if (selected is not null) closing = window.Closing;
                else
                {
                    var accountMonth = (await monthlyProjection.GetAsync(workspaceId, userId, new FinancialCalendarMonthlyProjectionQuery(month, 1, account.Id), ct, today)).Projection?.Months[0];
                    closing = accountMonth?.ClosingBalance;
                    if (closing is null && accountMonth?.UnavailableReason == BalanceUnavailableReason.BeforeOpeningBalanceDate && openings[account.Id]?.Date is { } accountFrom && accountFrom > month && accountFrom <= monthEnd)
                        closing = (await calendar.GetAsync(workspaceId, userId, new FinancialCalendarBalanceQuery(accountFrom, monthEnd, account.Id), ct)).Balances is { UnavailableReason: null } accountBalances ? accountBalances.Days[^1].ClosingBalance : null;
                }
            }
            decimal? difference = overview.TryGetValue(account.Id, out var recon) && recon.Difference is { } text ? decimal.Parse(text, CultureInfo.InvariantCulture) : null;
            overdraftResults.Add(PlanActualCalculator.Overdraft(new(account.Id, account.Name, accountFacts.OverdraftContractedLimit, accountFacts.OverdraftUsedLimit, current?.Amount, current?.ObservedAt,
                current?.AsOfDate, observations, month, today, closing, difference)));
        }
        var overdraft = PlanActualCalculator.ConsolidateOverdraft(overdraftResults, periodKind);

        // ---- Situação do mês -----------------------------------------------------------------------------------------------
        // Compromissos de meses anteriores ainda em aberto (vencidos ou a vencer antes do início do mês): a Agenda os soma ao saldo projetado, então o painel os mostra à parte.
        var carry = periodKind == "Past" ? null : await calendarSource.ReadAsync(workspaceId, selected?.Id, month.AddDays(-1), today, ct);
        var carryRows = carry is null ? [] : (carry.Pending ?? []).Concat(carry.CreditCardObligations ?? []).Concat(carry.DebtObligations ?? []).ToList();
        var situation = await BuildSituationAsync(workspaceId, inScope, today, m, window, openings, overview, periodKind, carryRows.Sum(r => r.Expense), carryRows.Sum(r => r.Income), ct);

        // ---- Sincronização -------------------------------------------------------------------------------------------------
        var sync = await BuildSyncAsync(workspaceId, userId, selected, facts, ct);

        var scopeDto = new PlanActualScopeDto(requested, kind, selected?.Id, selected?.Name, selected?.FinancialScope.ToString(), primary?.Id, primary?.Name,
            requested == PlanActualScopes.Primary && primary is null, ScopeOptions(all, facts.AccountsWithActivity.Concat(planAccounts).ToHashSet(), selected));
        return new(competence, today, periodKind, scopeDto, income, expenses, overdraft, cardsDto, situation, coverage, sync, true);
    }

    internal static IReadOnlyList<ScopeAccountDto> ScopeOptions(IReadOnlyList<FinancialAccountDto> all, IReadOnlySet<Guid> withActivity, FinancialAccountDto? selected) =>
        all.Where(a => a.IsActive || withActivity.Contains(a.Id) || a.Id == selected?.Id)
            .OrderByDescending(a => a.IsPrimary).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(a => new ScopeAccountDto(a.Id, a.Name, a.FinancialScope.ToString(), a.IsPrimary, a.IsActive)).ToList();

    private static CardTargetDto BuildCardTarget(FinancialAccountDto? selected, IReadOnlyList<CardSlice> slices, IReadOnlyList<CardProjection> projections, PlanActualFacts facts, DateOnly month, bool projectionComplete)
    {
        var reasons = new List<string>();
        if (slices.Count == 0) reasons.Add(PlanActualCalculator.Reasons.NoCards);
        if (slices.Any(s => s.PayerAccountId is null)) reasons.Add(PlanActualCalculator.Reasons.MissingPayerAccount);
        var cardReasons = projections.SelectMany(p => p.Coverage.Reasons).Where(r => r != CardCoverageReasons.MissingPayerAccount).Distinct().OrderBy(r => r, StringComparer.Ordinal).ToList();
        if (cardReasons.Count > 0 || !projectionComplete) reasons.Add(PlanActualCalculator.Reasons.CardCoverageIncomplete);
        reasons.AddRange(cardReasons.Select(r => "Cards:" + r));

        decimal? target = null; string? source = null; var partial = false;
        IReadOnlyList<Guid> payers;
        // Meta vigente de cada conta pagadora: a última vigência até a competência (um valor nulo encerra a meta).
        decimal? TargetOf(Guid payer) => facts.CardTargets.Where(t => t.PayerAccountId == payer && t.EffectiveFrom <= month).OrderByDescending(t => t.EffectiveFrom).FirstOrDefault()?.Amount;
        if (selected is not null)
        {
            payers = [selected.Id];
            target = TargetOf(selected.Id); source = target is null ? null : "CardTarget";
        }
        else
        {
            payers = slices.Where(s => s.PayerAccountId is not null).Select(s => s.PayerAccountId!.Value).Distinct().ToList();
            if (facts.AggregateCardTarget is { } aggregate) { target = aggregate; source = "AggregateTarget"; }
            else
            {
                var defined = payers.Select(TargetOf).Where(t => t is not null).Select(t => t!.Value).ToList();
                if (defined.Count > 0) { target = defined.Sum(); source = "CardTarget"; partial = defined.Count < payers.Count || slices.Any(s => s.PayerAccountId is null); }
            }
            if (partial) reasons.Add(PlanActualCalculator.Reasons.PartialPlan);
        }
        if (target is null && slices.Count > 0) reasons.Add(PlanActualCalculator.Reasons.PlanNotDefined);
        return PlanActualCalculator.CardTarget(slices, target, source, partial, payers, reasons);
    }

    /// <summary>Fechamento, menor saldo e primeira data negativa do mês, da Agenda. <c>PartialFrom</c> marca que só a parte do mês a partir do saldo inicial é calculável.</summary>
    private sealed record MonthWindow(decimal? Closing, decimal? Minimum, DateOnly? FirstNegative, FinancialCalendarBalances? Balances, DateOnly? PartialFrom);

    private async Task<SituationDto> BuildSituationAsync(
        Guid workspaceId, IReadOnlyList<FinancialAccountDto> inScope, DateOnly today, FinancialCalendarMonthlyProjectionMonth m, MonthWindow window,
        IReadOnlyDictionary<Guid, AccountOpeningBalance?> openings, IReadOnlyDictionary<Guid, AccountReconciliationDto> overview, string periodKind, decimal carryExpense, decimal carryIncome, CancellationToken ct)
    {
        // Saldo realizado de hoje: saldo inicial efetivo (manual ou derivado da referência bancária) + movimentos, pela mesma regra do núcleo (T06.3).
        var known = new List<decimal>(); var unavailable = 0;
        foreach (var account in inScope)
        {
            if (openings[account.Id] is not { Amount: { } amount, Date: { } date } || date > today) { unavailable++; continue; }
            var totals = await accounts.SumMovementsAsync(workspaceId, account.Id, date, today, ct);
            known.Add(FinancialBalanceCalculator.ApplyMovements(amount, totals.Income, totals.Expense, totals.TransferIn, totals.TransferOut));
        }
        var reasons = new List<string>();
        if (unavailable > 0) reasons.Add("RealizedBalanceUnavailable");
        if (window.PartialFrom is { } partialFrom) reasons.Add("PartialMonthFrom:" + partialFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        else if (m.UnavailableReason is { } reason) reasons.Add("ProjectionUnavailable:" + reason);
        if (carryExpense != 0m || carryIncome != 0m) reasons.Add("CarryoverIncluded");
        decimal? minimum = periodKind == "Past" ? null : window.Minimum;
        DateOnly? minimumDate = minimum is { } min && window.Balances is { Days: var days } ? days.Where(d => d.Kind == FinancialCalendarBalanceKind.Projected && d.ClosingBalance == min).Select(d => (DateOnly?)d.Date).FirstOrDefault() : null;
        var latestBank = inScope.Select(a => overview.TryGetValue(a.Id, out var o) ? o.Bank?.ObservedAt : null).Where(o => o is not null).Select(o => o!.Value).DefaultIfEmpty().Max();
        var state = reasons.Count == 0 ? PlanActualStates.Complete : PlanActualStates.Partial;
        return new(state, unavailable == 0 && known.Count > 0 ? PlanActualCalculator.Cents(known.Sum()) : null, known.Count > 0 ? PlanActualCalculator.Cents(known.Sum()) : null, known.Count, unavailable,
            PlanActualCalculator.Cents(window.Closing), PlanActualCalculator.Cents(minimum), minimumDate, PlanActualCalculator.Cents(m.PendingExpense), PlanActualCalculator.Cents(m.PendingIncome),
            PlanActualCalculator.Cents(carryExpense), PlanActualCalculator.Cents(carryIncome), window.FirstNegative, latestBank == default ? null : latestBank, reasons);
    }

    private async Task<SyncSummaryDto?> BuildSyncAsync(Guid workspaceId, Guid userId, FinancialAccountDto? selected, PlanActualFacts facts, CancellationToken ct)
    {
        var monitor = await syncMonitor.GetAsync(workspaceId, userId, ct);
        if (monitor.Access != PluggyAccess.Allowed || monitor.Value is not { } view) return null;
        var ids = facts.Connections.Where(c => selected is null || c.AccountId == selected.Id || c.CardPayerAccountId == selected.Id).Select(c => c.ConnectionId).ToHashSet();
        var connections = view.Connections.Where(c => ids.Contains(c.ConnectionId))
            .Select(c => new SyncConnectionDto(c.ConnectionId, c.Label, c.Status, c.LastSuccessAt, c.LastAttemptAt, c.NextAttemptAt, c.ConsentExpiringSoon)).ToList();
        return connections.Count == 0 ? null : PlanActualCalculator.Sync(connections, view.Auto.Enabled, view.Auto.IntervalHours, view.Now);
    }

    private static bool TryMonth(string text, out DateOnly month) =>
        DateOnly.TryParseExact(text + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out month) && text.Length == 7;
}
