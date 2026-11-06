using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Budgets;
using Saldo360.Domain.Budgets;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Budgets;

internal sealed class BudgetLayerReader(Saldo360DbContext db) : IBudgetLayerReader
{
    public Task<BudgetLayers> ReadAsync(Guid workspaceId, DateOnly from, DateOnly until, CancellationToken cancellationToken) => ReadAsync(db, workspaceId, from, until, cancellationToken);

    internal static async Task<BudgetLayers> ReadAsync(Saldo360DbContext db, Guid workspaceId, DateOnly from, DateOnly until, CancellationToken cancellationToken)
    {
        // Padrão: qualquer vigência até o fim do intervalo (a anterior ao início continua valendo). Exceção: só as competências do intervalo.
        var changes = await db.FinancialBudgetRuleChanges.AsNoTracking()
            .Where(c => c.WorkspaceId == workspaceId && c.Competence < until && (c.Scope == BudgetRuleScope.FromCompetence || c.Competence >= from))
            .Select(c => new RuleChangeFact(c.Scope, c.Target, c.CategoryId, c.Competence, c.Revision, c.Action, c.Amount, c.CreatedAt))
            .ToListAsync(cancellationToken);

        // Registros mensais históricos (orçamentos ativos; arquivado = como se não existisse). Metas arquivadas dentro deles são retiradas intencionais.
        var budgets = await db.FinancialBudgets.AsNoTracking()
            .Where(b => b.WorkspaceId == workspaceId && !b.IsArchived && b.Competence >= from && b.Competence < until)
            .Select(b => new { b.Id, b.Competence }).ToListAsync(cancellationToken);
        var budgetIds = budgets.Select(b => b.Id).ToList();
        var targets = budgetIds.Count == 0 ? [] : await db.FinancialBudgetTargets.AsNoTracking()
            .Where(t => t.WorkspaceId == workspaceId && budgetIds.Contains(t.BudgetId))
            .OrderBy(t => t.Id)
            .Select(t => new { t.Id, t.BudgetId, t.GroupingType, t.CategoryId, t.PlannedAmount, t.IsArchived }).ToListAsync(cancellationToken);
        var totals = await db.FinancialMonthlyAggregateTargets.AsNoTracking()
            .Where(t => t.WorkspaceId == workspaceId && t.TargetType == MonthlyAggregateTargetType.TotalExpenses && t.Competence >= from && t.Competence < until)
            .Select(t => new { t.Competence, t.Amount }).ToListAsync(cancellationToken);

        var months = budgets.Select(b => b.Competence).Concat(totals.Select(t => t.Competence)).Distinct();
        var legacy = months.ToDictionary(month => month, month =>
        {
            var budget = budgets.FirstOrDefault(b => b.Competence == month);
            var facts = budget is null ? [] : targets.Where(t => t.BudgetId == budget.Id).Select(t => new LegacyTargetFact(
                t.GroupingType switch { BudgetTargetGroupingType.AllCards => BudgetRuleTarget.AllCards, BudgetTargetGroupingType.FixedExpenses => BudgetRuleTarget.FixedExpenses, _ => BudgetRuleTarget.Category },
                t.CategoryId, t.PlannedAmount, t.IsArchived, t.Id)).ToList();
            return new LegacyMonthFact(month, budget?.Id, facts, totals.Where(t => t.Competence == month).Select(t => (decimal?)t.Amount).FirstOrDefault());
        });

        var categoryIds = changes.Where(c => c.CategoryId != null).Select(c => c.CategoryId!.Value)
            .Concat(legacy.Values.SelectMany(l => l.Targets).Where(t => t.CategoryId != null).Select(t => t.CategoryId!.Value)).Distinct().ToList();
        var parents = categoryIds.Count == 0 ? new Dictionary<Guid, Guid?>() : (await db.FinancialCategories.AsNoTracking()
            .Where(c => c.WorkspaceId == workspaceId && categoryIds.Contains(c.Id)).Select(c => new { c.Id, c.ParentCategoryId }).ToListAsync(cancellationToken))
            .ToDictionary(c => c.Id, c => c.ParentCategoryId);
        return new(changes, legacy, parents);
    }
}
