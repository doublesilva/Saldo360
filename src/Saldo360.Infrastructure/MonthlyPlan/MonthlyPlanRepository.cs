using Saldo360.Domain.Budgets;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Saldo360.Application.MonthlyPlan;
using Saldo360.Domain.Planning;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.MonthlyPlan;

/// <summary>Persistência do planejamento mensal (T10.2). Também grava o histórico na mesma unidade de trabalho das alterações (<see cref="IPlanHistoryWriter"/>).</summary>
internal sealed class MonthlyPlanRepository(Saldo360DbContext db) : IMonthlyPlanRepository, IPlanHistoryWriter
{
    public async Task<PlanFacts> LoadAsync(Guid workspaceId, DateOnly first, DateOnly last, CancellationToken ct)
    {
        var lines = await db.FinancialPlanLines.AsNoTracking()
            .Where(l => l.WorkspaceId == workspaceId && !l.IsArchived && l.Competence >= first && l.Competence <= last)
            .OrderBy(l => l.Competence).ThenBy(l => l.CreatedAt).ThenBy(l => l.Id)
            .Select(l => new PlanLineFact(l.Id, l.Competence, l.Kind, l.Nature, l.Description, l.AccountId, l.DestinationAccountId, l.CategoryId, l.OriginalAmount, l.Amount,
                l.ExpectedDate, l.HourlyRate, l.HoursPerDay, l.BillableDays, l.UpdatedAt))
            .ToListAsync(ct);

        // T12.1: os limites por categoria vêm do resolvedor ÚNICO de metas efetivas (exceção da competência, padrão vigente ou registro mensal histórico), então competências sem
        // orçamento mensal materializado mostram as metas herdadas, exceções substituem o padrão e metas suspensas continuam suspensas. Ler nunca grava nem cria orçamento.
        var layers = await Saldo360.Infrastructure.Budgets.BudgetLayerReader.ReadAsync(db, workspaceId, first, last.AddMonths(1), ct);
        var effective = new List<(DateOnly Competence, Guid BudgetId, Saldo360.Application.Budgets.EffectiveTarget Target)>();
        for (var month = first; month <= last; month = month.AddMonths(1))
        {
            var resolved = Saldo360.Application.Budgets.EffectiveBudgetResolver.Resolve(workspaceId, layers, month);
            effective.AddRange(resolved.Categories.Select(t => (month, resolved.BudgetId ?? Guid.Empty, t)));
        }
        var categoryIds = effective.Select(e => e.Target.CategoryId!.Value).Distinct().ToList();
        var categoryNames = categoryIds.Count == 0 ? new Dictionary<Guid, string>()
            : await db.FinancialCategories.AsNoTracking().Where(c => c.WorkspaceId == workspaceId && categoryIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var limits = effective.Where(e => categoryNames.ContainsKey(e.Target.CategoryId!.Value))
            .Select(e => new { Id = e.Target.Id, BudgetId = e.BudgetId, Competence = e.Competence, CategoryId = e.Target.CategoryId!.Value, CategoryName = categoryNames[e.Target.CategoryId!.Value], PlannedAmount = e.Target.Amount!.Value })
            .OrderBy(l => l.Competence).ThenBy(l => l.CategoryName, StringComparer.CurrentCultureIgnoreCase).ThenBy(l => l.Id).ToList();

        // O valor original de um limite é o da primeira entrada do histórico: o criado, ou o anterior à primeira revisão (metas anteriores ao histórico).
        var ids = limits.Select(l => l.Id).ToList();
        var firstEntries = ids.Count == 0 ? [] : await db.FinancialPlanHistory.AsNoTracking()
            .Where(h => h.WorkspaceId == workspaceId && h.Subject == PlanHistorySubject.CategoryLimit && ids.Contains(h.SubjectId))
            .Select(h => new { h.SubjectId, h.At, h.Id, h.PreviousAmount, h.NewAmount }).ToListAsync(ct);
        var originals = firstEntries.GroupBy(h => h.SubjectId)
            .ToDictionary(g => g.Key, g => { var first = g.OrderBy(h => h.At).ThenBy(h => h.Id).First(); return first.PreviousAmount ?? first.NewAmount; });

        return new(lines, limits.Select(l => new PlanLimitFact(l.Id, l.BudgetId, l.Competence, l.CategoryId, l.CategoryName, l.PlannedAmount,
            originals.TryGetValue(l.Id, out var original) ? original : null)).ToList());
    }

    public Task<FinancialPlanLine?> FindLineAsync(Guid workspaceId, Guid lineId, CancellationToken ct) =>
        db.FinancialPlanLines.SingleOrDefaultAsync(l => l.WorkspaceId == workspaceId && l.Id == lineId, ct);

    public async Task<IReadOnlySet<Guid>> AccountsWithPlanAsync(Guid workspaceId, DateOnly first, DateOnly last, CancellationToken ct)
    {
        var rows = await db.FinancialPlanLines.AsNoTracking()
            .Where(l => l.WorkspaceId == workspaceId && !l.IsArchived && l.Competence >= first && l.Competence <= last)
            .Select(l => new { l.AccountId, l.DestinationAccountId }).ToListAsync(ct);
        return rows.SelectMany(r => new[] { r.AccountId, r.DestinationAccountId }).Where(id => id is not null).Select(id => id!.Value).ToHashSet();
    }

    public void Add(FinancialPlanLine line) => db.FinancialPlanLines.Add(line);

    public void Stage(FinancialPlanHistoryEntry entry) => db.FinancialPlanHistory.Add(entry);

    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    public async Task<IReadOnlyList<FinancialPlanHistoryEntry>> HistoryAsync(Guid workspaceId, DateOnly competence, Guid? subjectId, int limit, CancellationToken ct) =>
        await db.FinancialPlanHistory.AsNoTracking()
            .Where(h => h.WorkspaceId == workspaceId && h.Competence == competence && (subjectId == null || h.SubjectId == subjectId))
            .OrderByDescending(h => h.At).ThenByDescending(h => h.Id).Take(limit).ToListAsync(ct);

    public async Task<IReadOnlyList<FinancialNonWorkingDay>> ListNonWorkingDaysAsync(Guid workspaceId, DateOnly first, DateOnly last, CancellationToken ct) =>
        await db.FinancialNonWorkingDays.AsNoTracking().Where(d => d.WorkspaceId == workspaceId && d.Date >= first && d.Date <= last).OrderBy(d => d.Date).ToListAsync(ct);

    public async Task<bool> AddNonWorkingDayAsync(FinancialNonWorkingDay day, CancellationToken ct)
    {
        db.FinancialNonWorkingDays.Add(day);
        return await TrySaveAsync(ct);
    }

    public async Task<bool> RemoveNonWorkingDayAsync(Guid workspaceId, Guid id, CancellationToken ct)
    {
        var day = await db.FinancialNonWorkingDays.SingleOrDefaultAsync(d => d.WorkspaceId == workspaceId && d.Id == id, ct);
        if (day is null) return false;
        db.FinancialNonWorkingDays.Remove(day);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> ApplyCopyAsync(CopyBatch batch, CancellationToken ct)
    {
        if (batch.NewBudget is not null) db.FinancialBudgets.Add(batch.NewBudget);
        db.FinancialBudgetTargets.AddRange(batch.Targets);
        db.FinancialPlanLines.AddRange(batch.Lines);
        db.FinancialPlanHistory.AddRange(batch.History);
        return await TrySaveAsync(ct);
    }

    // ---- PROJ-01 ----------------------------------------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<ProjectedPjTransfer>> ProjectedPjTransfersAsync(Guid workspaceId, DateOnly today, CancellationToken cancellationToken, DateOnly? through = null) =>
        (await Saldo360.Infrastructure.Calendar.PjTransferProjection.PlanAsync(db, workspaceId, today, through ?? Saldo360.Infrastructure.Calendar.PjTransferProjection.Horizon(today), cancellationToken))?.Transfers ?? [];

    public async Task<bool> AreExpenseCategoriesAsync(Guid workspaceId, IReadOnlyCollection<Guid> categoryIds, CancellationToken ct) =>
        await db.FinancialCategories.AsNoTracking().CountAsync(c => c.WorkspaceId == workspaceId && c.Type == Saldo360.Domain.Categories.CategoryType.Expense && categoryIds.Contains(c.Id), ct) == categoryIds.Count;

    public async Task UpsertFoodProvisionAsync(Guid workspaceId, DateOnly? competence, decimal amount, IReadOnlyCollection<Guid> categoryIds, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var existing = await db.FinancialFoodProvisions.SingleOrDefaultAsync(r => r.WorkspaceId == workspaceId && r.Competence == competence, ct);
        if (existing is null) db.FinancialFoodProvisions.Add(Saldo360.Domain.Planning.FinancialFoodProvision.Create(workspaceId, competence, amount, categoryIds, userId, now));
        else existing.Revise(amount, categoryIds, userId, now);
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveFoodProvisionMonthAsync(Guid workspaceId, DateOnly competence, CancellationToken ct)
    {
        var existing = await db.FinancialFoodProvisions.SingleOrDefaultAsync(r => r.WorkspaceId == workspaceId && r.Competence == competence, ct);
        if (existing is null) return;
        db.FinancialFoodProvisions.Remove(existing);
        await db.SaveChangesAsync(ct);
    }

    public async Task<FoodProvisionFacts> FoodProvisionAsync(Guid workspaceId, DateOnly from, DateOnly through, CancellationToken ct)
    {
        var rows = await db.FinancialFoodProvisions.AsNoTracking().Where(r => r.WorkspaceId == workspaceId).ToListAsync(ct);
        var standard = rows.FirstOrDefault(r => r.Competence == null);
        var categories = standard?.CategoryIds.ToHashSet() ?? [];
        var inferred = false;
        if (categories.Count == 0)
        {
            // A coluna de nome usa collation não determinística (sem LIKE/ILIKE): o filtro por nome é feito em memória sobre as categorias de despesa do orçamento.
            categories = (await db.FinancialCategories.AsNoTracking().Where(c => c.WorkspaceId == workspaceId && c.Type == Saldo360.Domain.Categories.CategoryType.Expense)
                .Select(c => new { c.Id, c.Name }).ToListAsync(ct)).Where(c => c.Name.Contains("aliment", StringComparison.OrdinalIgnoreCase)).Select(c => c.Id).ToHashSet();
            inferred = categories.Count > 0;
        }
        var known = new Dictionary<DateOnly, decimal>();
        if (categories.Count > 0)
        {
            var competences = Enumerable.Range(0, Math.Max(1, (through.Year - from.Year) * 12 + through.Month - from.Month + 1)).Select(i => from.AddMonths(i).ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture)).ToList();
            var amounts = await (from i in db.FinancialCreditCardInstallments.AsNoTracking()
                                 join p in db.FinancialCreditCardPurchases.AsNoTracking() on i.PurchaseId equals p.Id
                                 join v in db.FinancialCreditCardInvoices.AsNoTracking() on i.InvoiceId equals v.Id
                                 join card in db.FinancialCreditCards.AsNoTracking() on p.CreditCardId equals card.Id
                                 join payer in db.FinancialAccounts.AsNoTracking() on card.DefaultPaymentAccountId equals (Guid?)payer.Id
                                 // Só cartões pagos por conta PF: o alimento de cartão PJ ou sem pagador não abate a reserva do orçamento PF.
                                 where payer.Scope == Saldo360.Domain.Accounts.FinancialScope.PF && i.WorkspaceId == workspaceId && p.WorkspaceId == workspaceId && i.Status == Saldo360.Domain.CreditCards.CreditCardInstallmentStatus.Active
                                    && p.Status == Saldo360.Domain.CreditCards.CreditCardPurchaseStatus.Active && competences.Contains(v.Competence)
                                    && categories.Contains(i.OfficialCategoryId ?? p.CategoryId)
                                 group i.Amount by v.Competence into g
                                 select new { Competence = g.Key, Amount = g.Sum() }).ToListAsync(ct);
            foreach (var a in amounts) known[DateOnly.ParseExact(a.Competence + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)] = a.Amount;
        }
        return new(standard?.MonthlyAmount ?? Saldo360.Domain.Planning.FinancialFoodProvision.DefaultMonthlyAmount, standard is not null, categories, inferred,
            rows.Where(r => r.Competence != null).ToDictionary(r => r.Competence!.Value, r => r.MonthlyAmount), known);
    }

    public Task<IncomeForecastView> LoadIncomeForecastAsync(Guid workspaceId, DateOnly from, DateOnly through, DateOnly today, CancellationToken ct) =>
        IncomeForecastLoader.LoadAsync(db, workspaceId, from, through, today, ct);

    public async Task UpsertIncomeForecastAsync(Guid workspaceId, IncomeForecastSettings s, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var existing = await db.FinancialIncomeForecasts.SingleOrDefaultAsync(f => f.WorkspaceId == workspaceId, ct);
        if (existing is null) db.FinancialIncomeForecasts.Add(FinancialIncomeForecast.Create(workspaceId, s.AccountId, s.HourlyRate, s.HoursPerDay, s.PaymentDay, s.PaymentOffsetMonths, s.TaxReservePercent, s.OperationalReserve, s.IsActive, userId, now));
        else existing.Revise(s.AccountId, s.HourlyRate, s.HoursPerDay, s.PaymentDay, s.PaymentOffsetMonths, s.TaxReservePercent, s.OperationalReserve, s.IsActive, userId, now);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpsertForecastMonthAsync(Guid workspaceId, IncomeForecastMonthOverride a, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var existing = await db.FinancialIncomeForecastMonths.SingleOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.Competence == a.Competence, ct);
        if (existing is null) db.FinancialIncomeForecastMonths.Add(FinancialIncomeForecastMonth.Create(workspaceId, a.Competence, a.BillableDays, a.ExpectedAmount, a.PaymentDate, a.IsDismissed, userId, now));
        else existing.Revise(a.BillableDays, a.ExpectedAmount, a.PaymentDate, a.IsDismissed, userId, now);
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> RemoveForecastMonthAsync(Guid workspaceId, DateOnly competence, CancellationToken ct)
    {
        var existing = await db.FinancialIncomeForecastMonths.SingleOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.Competence == competence, ct);
        if (existing is null) return false;
        db.FinancialIncomeForecastMonths.Remove(existing);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<TransferMonthFlow>> TransferFlowsAsync(Guid workspaceId, Guid sourceAccountId, Guid destinationAccountId, DateOnly from, DateOnly through, CancellationToken ct)
    {
        var planned = await db.FinancialPlannedTransfers.AsNoTracking()
            .Where(t => t.WorkspaceId == workspaceId && !t.IsCancelled && t.RealizedTransferId == null && t.SourceAccountId == sourceAccountId && t.DestinationAccountId == destinationAccountId
                && t.DueDate >= from && t.DueDate <= through)
            .Select(t => new { t.DueDate, t.Amount }).ToListAsync(ct);
        var realized = await db.FinancialTransfers.AsNoTracking()
            .Where(t => t.WorkspaceId == workspaceId && !t.IsCancelled && t.SourceAccountId == sourceAccountId && t.DestinationAccountId == destinationAccountId
                && t.TransferDate >= from && t.TransferDate <= through)
            .Select(t => new { t.TransferDate, t.Amount }).ToListAsync(ct);
        var months = planned.Select(p => new DateOnly(p.DueDate.Year, p.DueDate.Month, 1)).Concat(realized.Select(r => new DateOnly(r.TransferDate.Year, r.TransferDate.Month, 1))).Distinct();
        return months.Select(m => new TransferMonthFlow(m,
            planned.Where(p => p.DueDate.Year == m.Year && p.DueDate.Month == m.Month).Sum(p => p.Amount),
            realized.Where(r => r.TransferDate.Year == m.Year && r.TransferDate.Month == m.Month).Sum(r => r.Amount))).ToList();
    }

    public async Task<IReadOnlyDictionary<DateOnly, decimal>> PendingFixedByMonthAsync(Guid workspaceId, Guid accountId, DateOnly from, DateOnly through, DateOnly today, CancellationToken ct) =>
        (await Saldo360.Infrastructure.Recurrences.FixedObligationReader.LoadAsync(db, workspaceId, from, through, today, ct))
            .Where(o => o.AccountId == accountId && o.Type == Saldo360.Domain.Categories.CategoryType.Expense && o.IsPendingCashFlow)
            .GroupBy(o => new DateOnly(o.DueDate.Year, o.DueDate.Month, 1)).ToDictionary(g => g.Key, g => g.Sum(o => o.PlannedAmount));

    private async Task<bool> TrySaveAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); return true; }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }
}
