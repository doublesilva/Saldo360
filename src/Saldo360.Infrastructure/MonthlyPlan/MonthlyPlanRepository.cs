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

        var limits = await (from t in db.FinancialBudgetTargets.AsNoTracking()
                            join b in db.FinancialBudgets.AsNoTracking() on t.BudgetId equals b.Id
                            join c in db.FinancialCategories.AsNoTracking() on t.CategoryId equals c.Id
                            where t.WorkspaceId == workspaceId && t.GroupingType == BudgetTargetGroupingType.Category && t.CategoryId != null && !t.IsArchived && !b.IsArchived && b.Competence >= first && b.Competence <= last
                            orderby b.Competence, c.Name, t.Id
                            select new { t.Id, t.BudgetId, b.Competence, CategoryId = t.CategoryId!.Value, CategoryName = c.Name, t.PlannedAmount }).ToListAsync(ct);

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
