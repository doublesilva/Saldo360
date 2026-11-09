using System.Data;
using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Budgets;
using Saldo360.Domain.Budgets;
using Saldo360.Domain.Categories;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Budgets;

internal sealed class FinancialBudgetExecutionRepository(Saldo360DbContext db, TimeProvider? time = null)
    : IFinancialBudgetExecutionRepository
{
    private DateOnly Today => DateOnly.FromDateTime((time ?? TimeProvider.System).GetUtcNow().UtcDateTime);

    public async Task<FinancialBudgetExecutionSource?> GetAsync(
        Guid workspaceId, Guid budgetId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);
        var result = await ReadRangeAsync(db, workspaceId, null, null, budgetId, cancellationToken, Today);
        await transaction.CommitAsync(cancellationToken);
        return result.SingleOrDefault();
    }

    public async Task<BudgetCompositionSource?> GetCompositionAsync(
        Guid workspaceId, Guid budgetId, Guid targetId, CancellationToken cancellationToken)
    {
        var competence = await db.FinancialBudgets.AsNoTracking().Where(item => item.WorkspaceId == workspaceId && item.Id == budgetId && !item.IsArchived)
            .Select(item => (DateOnly?)item.Competence).SingleOrDefaultAsync(cancellationToken);
        return competence is { } month ? await ComposeAsync(workspaceId, month, targetId, cancellationToken) : null;
    }

    public Task<BudgetCompositionSource?> GetEffectiveCompositionAsync(Guid workspaceId, DateOnly competence, Guid targetId, CancellationToken cancellationToken) =>
        ComposeAsync(workspaceId, competence, targetId, cancellationToken);

    public async Task<FinancialBudgetExecutionSource?> GetEffectiveAsync(Guid workspaceId, DateOnly competence, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);
        var result = await ReadRangeAsync(db, workspaceId, competence, competence.AddMonths(1), null, cancellationToken, Today, includeEmptyMonths: true);
        await transaction.CommitAsync(cancellationToken);
        return result.SingleOrDefault();
    }

    private async Task<BudgetCompositionSource?> ComposeAsync(Guid workspaceId, DateOnly competence, Guid targetId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);
        var layers = await BudgetLayerReader.ReadAsync(db, workspaceId, competence, competence.AddMonths(1), cancellationToken);
        var effective = EffectiveBudgetResolver.Resolve(workspaceId, layers, competence);
        var target = effective.Targets.FirstOrDefault(item => item.Id == targetId && item.Defined && item.Target != BudgetRuleTarget.TotalExpenses);
        if (target is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var grouping = ToGrouping(target.Target);
        var parentOf = await BudgetFactReader.ParentsAsync(db, workspaceId, target.CategoryId is { } category ? [category] : [], cancellationToken);
        var month = await BudgetFactReader.ReadAsync(db, workspaceId, competence, competence.AddMonths(1), cancellationToken, Today);
        var covered = month.Where(fact => BudgetTargetMatcher.Covers(grouping, target.CategoryId, parentOf, fact))
            .OrderBy(fact => fact.Date).ThenBy(fact => fact.Description, StringComparer.CurrentCultureIgnoreCase).ThenBy(fact => fact.RecordId).ToList();
        var hasCards = await HasCardsAsync(db, workspaceId, cancellationToken);
        var coverage = CoverageOf(grouping, covered, month, hasCards);
        var ids = covered.Where(fact => fact.CategoryId != null).Select(fact => fact.CategoryId!.Value).Distinct().ToList();
        var categories = await db.FinancialCategories.AsNoTracking().Where(c => c.WorkspaceId == workspaceId && ids.Contains(c.Id))
            .Select(c => new { c.Id, c.Name, ParentName = db.FinancialCategories.Where(p => p.Id == c.ParentCategoryId).Select(p => p.Name).FirstOrDefault() }).ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(effective.BudgetId ?? Guid.Empty, competence, target.Id, grouping, target.CategoryId, target.Amount!.Value, covered, coverage,
            categories.ToDictionary(c => c.Id, c => c.ParentName is null ? c.Name : $"{c.ParentName} / {c.Name}"));
    }

    internal static BudgetTargetGroupingType ToGrouping(BudgetRuleTarget target) => target switch
    {
        BudgetRuleTarget.AllCards => BudgetTargetGroupingType.AllCards,
        BudgetRuleTarget.FixedExpenses => BudgetTargetGroupingType.FixedExpenses,
        _ => BudgetTargetGroupingType.Category,
    };

    public async Task<(IReadOnlyList<BudgetFact> Facts, IReadOnlyDictionary<Guid, string> CategoryPaths)> ReadFactsAsync(Guid workspaceId, DateOnly first, DateOnly through, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);
        var facts = await BudgetFactReader.ReadAsync(db, workspaceId, first, through, cancellationToken, Today);
        var ids = facts.Where(fact => fact.CategoryId != null).Select(fact => fact.CategoryId!.Value).Distinct().ToList();
        var categories = await db.FinancialCategories.AsNoTracking().Where(c => c.WorkspaceId == workspaceId && ids.Contains(c.Id))
            .Select(c => new { c.Id, c.Name, ParentName = db.FinancialCategories.Where(p => p.Id == c.ParentCategoryId).Select(p => p.Name).FirstOrDefault() }).ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (facts, categories.ToDictionary(c => c.Id, c => c.ParentName is null ? c.Name : $"{c.ParentName} / {c.Name}"));
    }

    private static Task<bool> HasCardsAsync(Saldo360DbContext db, Guid workspaceId, CancellationToken cancellationToken) =>
        db.FinancialCreditCards.AsNoTracking().AnyAsync(card => card.WorkspaceId == workspaceId && !card.IsArchived, cancellationToken);

    /// <summary>
    /// Sinais de dados incompletos: competência estimada nos cartões e despesas sem tipo definido (que não entram nas fixas). Ausência de dado nunca
    /// vira "zero confirmado": sem cartões cadastrados a meta de cartões é marcada como indisponível.
    /// </summary>
    internal static BudgetCoverage CoverageOf(BudgetTargetGroupingType grouping, IReadOnlyList<BudgetFact> covered, IReadOnlyList<BudgetFact> month, bool hasCards)
    {
        var estimated = covered.Count(fact => fact.IsCard && !fact.CompetenceReliable);
        if (grouping != BudgetTargetGroupingType.FixedExpenses)
            return new(estimated, NoCards: grouping == BudgetTargetGroupingType.AllCards && !hasCards);
        var undefined = month.Where(fact => fact.Nature == ExpenseNature.Undefined && fact.Amount > 0m).ToList();
        var classifiable = undefined.Where(fact => fact.IsClassifiable).ToList();
        return new(estimated, classifiable.Count, classifiable.Sum(fact => fact.Amount), UnclassifiableCount: undefined.Count - classifiable.Count);
    }

    /// <summary>
    /// Execução por competência sobre as metas EFETIVAS (T12.1). Uma competência entra se tiver orçamento mensal histórico, se tiver ao menos uma meta definida (herdada ou exceção) ou se
    /// <paramref name="includeEmptyMonths"/>. Com <paramref name="budgetId"/>, só a competência daquele orçamento. O identificador de uma meta é o da meta histórica ou o estável da chave.
    /// </summary>
    internal static async Task<IReadOnlyList<FinancialBudgetExecutionSource>> ReadRangeAsync(
        Saldo360DbContext db, Guid workspaceId, DateOnly? from, DateOnly? until, Guid? budgetId, CancellationToken cancellationToken, DateOnly? today = null, bool includeEmptyMonths = false)
    {
        DateOnly low, high;
        if (budgetId is { } id)
        {
            var competence = await db.FinancialBudgets.AsNoTracking().Where(item => item.WorkspaceId == workspaceId && item.Id == id && !item.IsArchived)
                .Select(item => (DateOnly?)item.Competence).SingleOrDefaultAsync(cancellationToken);
            if (competence is not { } found) return [];
            (low, high) = (found, found.AddMonths(1));
        }
        else if (from is { } start && until is { } end) (low, high) = (start, end);
        else
        {
            var all = await db.FinancialBudgets.AsNoTracking().Where(item => item.WorkspaceId == workspaceId && !item.IsArchived).Select(item => item.Competence).ToListAsync(cancellationToken);
            if (all.Count == 0) return [];
            (low, high) = (all.Min(), all.Max().AddMonths(1));
        }

        var layers = await BudgetLayerReader.ReadAsync(db, workspaceId, low, high, cancellationToken);
        var months = new List<(EffectiveBudgetMonth Month, List<EffectiveTarget> Targets)>();
        for (var month = low; month < high; month = month.AddMonths(1))
        {
            var effective = EffectiveBudgetResolver.Resolve(workspaceId, layers, month);
            var targets = effective.Targets.Where(item => item.Defined && item.Target != BudgetRuleTarget.TotalExpenses).OrderBy(item => item.Id).ToList();
            if (includeEmptyMonths || budgetId is not null || effective.BudgetId is not null || targets.Count > 0) months.Add((effective, targets));
        }
        if (months.Count == 0) return [];

        var first = months.Min(x => x.Month.Competence);
        var through = months.Max(x => x.Month.Competence).AddMonths(1);
        // Meta de categoria principal cobre os registros diretos e os das subcategorias (R02): cada registro tem UM categoryId, então é contado uma só vez.
        var parentOf = await BudgetFactReader.ParentsAsync(db, workspaceId, months.SelectMany(x => x.Targets).Where(item => item.CategoryId != null).Select(item => item.CategoryId!.Value).Distinct().ToList(), cancellationToken);
        var facts = await BudgetFactReader.ReadAsync(db, workspaceId, first, through, cancellationToken, today);
        var hasCards = await HasCardsAsync(db, workspaceId, cancellationToken);

        return months.Select(entry =>
        {
            var competence = entry.Month.Competence;
            var month = facts.Where(fact => fact.Date.Year == competence.Year && fact.Date.Month == competence.Month).ToList();
            return new FinancialBudgetExecutionSource(entry.Month.BudgetId ?? Guid.Empty, competence,
                entry.Targets.Select(item =>
                {
                    var grouping = ToGrouping(item.Target);
                    var covered = month.Where(fact => BudgetTargetMatcher.Covers(grouping, item.CategoryId, parentOf, fact)).ToList();
                    return new FinancialBudgetExecutionSourceTarget(item.Id, item.CategoryId, item.Amount!.Value,
                        covered.Where(fact => fact.State == BudgetFactState.Realized).Sum(fact => fact.Amount),
                        covered.Where(fact => fact.State == BudgetFactState.Pending).Sum(fact => fact.Amount),
                        covered.Where(fact => fact.State == BudgetFactState.CardLaunched).Sum(fact => fact.Amount),
                        grouping, CoverageOf(grouping, covered, month, hasCards));
                }).ToList());
        }).ToList();
    }
}
