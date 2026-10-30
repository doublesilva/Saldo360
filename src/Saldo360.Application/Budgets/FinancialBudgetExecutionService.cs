using Saldo360.Application.Workspaces;
using Saldo360.Domain.Budgets;

namespace Saldo360.Application.Budgets;

public sealed class FinancialBudgetExecutionService(
    IFinancialWorkspaceRepository workspaces,
    IFinancialBudgetExecutionRepository executions)
{
    public const int PercentageScale = 2;
    public const decimal ApproachingThreshold = 0.80m;
    public const int DefaultCompositionPageSize = 20;
    public const int MaxCompositionPageSize = 100;

    public async Task<FinancialBudgetExecutionDto?> GetAsync(
        Guid workspaceId, Guid budgetId, Guid userId, CancellationToken cancellationToken)
    {
        if (await workspaces.GetOwnerRoleAsync(workspaceId, userId, cancellationToken) is null) return null;
        var source = await executions.GetAsync(workspaceId, budgetId, cancellationToken);
        if (source is null) return null;

        var targets = source.Targets
            .Select(item =>
            {
                var amounts = Calculate(item.Planned, item.Realized, item.Committed, item.CreditCardCommitted);
                return new FinancialBudgetTargetExecutionDto(
                    item.TargetId, item.CategoryId, amounts, Classify(item.Planned, amounts.Consumed), Message(Classify(item.Planned, amounts.Consumed)),
                    item.GroupingType, Classify(item.Planned, amounts.ProjectedConsumption), item.Coverage);
            })
            .ToList();
        // Só as metas de categoria (que não se sobrepõem) formam um resumo conjunto. Fixas e cartões contam os mesmos gastos por outra perspectiva
        // e nunca entram na soma.
        var categories = source.Targets.Where(item => item.GroupingType == BudgetTargetGroupingType.Category).ToList();
        var categoryTargets = targets.Where(item => item.GroupingType == BudgetTargetGroupingType.Category).ToList();
        var total = Calculate(
            categories.Sum(item => item.Planned), categories.Sum(item => item.Realized),
            categories.Sum(item => item.Committed), categories.Sum(item => item.CreditCardCommitted));
        var totalStatus = categoryTargets.Count == 0 ? FinancialBudgetAlertStatus.WithinBudget : categoryTargets.Max(item => item.Status);
        var summary = new FinancialBudgetExecutionSummary(
            total.Planned, total.Realized, total.Committed, total.ProjectedConsumption, total.Available, total.ExceededAmount, total.ConsumptionPercentage,
            totalStatus, categoryTargets.Count == 0 ? "Nenhuma meta de categoria ativa para esta competência." : Message(totalStatus),
            categoryTargets.Count(item => item.Status == FinancialBudgetAlertStatus.ApproachingLimit),
            categoryTargets.Count(item => item.Status == FinancialBudgetAlertStatus.LimitReached),
            categoryTargets.Count(item => item.Status == FinancialBudgetAlertStatus.Exceeded),
            total.CreditCardCommitted, total.Consumed, total.AvailableProjected, categoryTargets.Count, total.ExceededProjected, total.ProjectedPercentage,
            categoryTargets.Count == 0 ? FinancialBudgetAlertStatus.WithinBudget : categoryTargets.Max(item => item.ProjectedStatus));
        return new FinancialBudgetExecutionDto(source.BudgetId, source.Competence, summary, targets);
    }

    /// <summary>Registros que compõem uma meta, paginados. O total e os valores vêm da mesma lista do resumo.</summary>
    public async Task<BudgetCompositionDto?> GetCompositionAsync(
        Guid workspaceId, Guid budgetId, Guid targetId, Guid userId, int? page, int? pageSize, CancellationToken cancellationToken)
    {
        if (await workspaces.GetOwnerRoleAsync(workspaceId, userId, cancellationToken) is null) return null;
        var source = await executions.GetCompositionAsync(workspaceId, budgetId, targetId, cancellationToken);
        if (source is null) return null;

        var realized = source.Facts.Where(f => f.State == BudgetFactState.Realized).Sum(f => f.Amount);
        var pending = source.Facts.Where(f => f.State == BudgetFactState.Pending).Sum(f => f.Amount);
        var launched = source.Facts.Where(f => f.State == BudgetFactState.CardLaunched).Sum(f => f.Amount);
        var amounts = Calculate(source.Planned, realized, pending, launched);
        var size = Math.Clamp(pageSize ?? DefaultCompositionPageSize, 1, MaxCompositionPageSize);
        var number = Math.Max(1, page ?? 1);
        var items = source.Facts.Skip((number - 1) * size).Take(size)
            .Select(f => new BudgetCompositionItemDto(f.Origin, f.RecordId, f.Date, f.Amount, f.State, f.CategoryId,
                f.CategoryId is { } c && source.CategoryPaths.TryGetValue(c, out var path) ? path : null, f.Nature, f.Description,
                f.CardId, f.CardName, f.AccountName, f.InvoiceCompetence, f.CompetenceReliable)).ToList();
        // Participação de cada cartão: só informativa (não há teto individual).
        var cards = source.Facts.Where(f => f.CardId is not null && f.State == BudgetFactState.CardLaunched).GroupBy(f => f.CardId!.Value)
            .Select(g => new BudgetCompositionCardDto(g.Key, g.First().CardName ?? string.Empty, g.Sum(f => f.Amount), g.Count()))
            .OrderByDescending(c => c.Consumed).ThenBy(c => c.CardName, StringComparer.CurrentCultureIgnoreCase).ToList();
        return new BudgetCompositionDto(source.BudgetId, source.Competence, source.TargetId, source.GroupingType, amounts, Classify(source.Planned, amounts.Consumed),
            source.Coverage, number, size, source.Facts.Count, items, cards);
    }

    public static FinancialBudgetAlertStatus Classify(decimal planned, decimal projected)
    {
        if (planned == 0m) return projected == 0m
            ? FinancialBudgetAlertStatus.WithinBudget
            : FinancialBudgetAlertStatus.Exceeded;
        var ratio = projected / planned;
        if (ratio > 1m) return FinancialBudgetAlertStatus.Exceeded;
        if (ratio == 1m) return FinancialBudgetAlertStatus.LimitReached;
        return ratio >= ApproachingThreshold
            ? FinancialBudgetAlertStatus.ApproachingLimit
            : FinancialBudgetAlertStatus.WithinBudget;
    }

    public static string Message(FinancialBudgetAlertStatus status) => status switch
    {
        FinancialBudgetAlertStatus.WithinBudget => "O consumo está dentro do limite.",
        FinancialBudgetAlertStatus.ApproachingLimit => "O consumo está próximo do limite.",
        FinancialBudgetAlertStatus.LimitReached => "O consumo atingiu o limite.",
        FinancialBudgetAlertStatus.Exceeded => "O consumo ultrapassou o limite.",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    /// <summary>
    /// Consumido = efetivo + cartão lançado. Previsto adicional (<paramref name="committed"/>) é o que está cadastrado e ainda não consumiu. Projetado =
    /// consumido + previsto. Disponível = limite − consumido; disponível projetado = limite − projetado. Limite zero não divide.
    /// </summary>
    public static FinancialBudgetExecutionAmounts Calculate(decimal planned, decimal realized, decimal committed, decimal creditCardCommitted = 0m)
    {
        var consumed = realized + creditCardCommitted;
        var projected = consumed + committed;
        decimal? Percent(decimal value) => planned == 0m
            ? value == 0m ? 0m : null
            : decimal.Round(value / planned * 100m, PercentageScale, MidpointRounding.AwayFromZero);
        return new FinancialBudgetExecutionAmounts(
            planned, realized, committed, projected, planned - consumed, Math.Max(0m, consumed - planned), Percent(consumed), creditCardCommitted,
            consumed, planned - projected, Math.Max(0m, projected - planned), Percent(projected));
    }
}
