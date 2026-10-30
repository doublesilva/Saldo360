using System.Globalization;
using Saldo360.Application.MonthlyPlan;
using Saldo360.Application.Workspaces;
using Saldo360.Domain.Budgets;
using Saldo360.Domain.Common;
using Saldo360.Domain.Planning;

namespace Saldo360.Application.Budgets;

public sealed class FinancialBudgetService(
    IFinancialWorkspaceRepository workspaces,
    IFinancialBudgetRepository budgets,
    IPlanHistoryWriter history,
    TimeProvider timeProvider)
{
    public async Task<BudgetResult> CreateAsync(
        Guid workspaceId, Guid userId, string? competence, string? description, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(workspaceId, userId, cancellationToken)) return BudgetResult.NotFound;
        var errors = ValidateBudget(competence, description, out var month, out var normalizedDescription);
        if (errors.Count > 0) return BudgetResult.Invalid(errors);
        if (await budgets.BudgetExistsAsync(workspaceId, month, cancellationToken))
            return BudgetResult.Conflict(BudgetResult.DuplicateBudgetMessage);
        var budget = FinancialBudget.Create(workspaceId, month, normalizedDescription, userId, timeProvider.GetUtcNow());
        return await budgets.TryAddBudgetAsync(budget, cancellationToken)
            ? BudgetResult.Ok((await budgets.GetAsync(workspaceId, budget.Id, cancellationToken))!)
            : BudgetResult.Conflict(BudgetResult.DuplicateBudgetMessage);
    }

    public async Task<IReadOnlyList<FinancialBudgetDto>?> ListAsync(
        Guid workspaceId, Guid userId, string? competence, bool includeArchived, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(workspaceId, userId, cancellationToken)) return null;
        DateOnly? month = null;
        if (competence is not null)
        {
            if (!TryParseCompetence(competence, out var parsed)) return [];
            month = parsed;
        }
        return await budgets.ListAsync(workspaceId, month, includeArchived, cancellationToken);
    }

    public async Task<FinancialBudgetDto?> GetAsync(
        Guid workspaceId, Guid budgetId, Guid userId, CancellationToken cancellationToken) =>
        await IsParticipantAsync(workspaceId, userId, cancellationToken)
            ? await budgets.GetAsync(workspaceId, budgetId, cancellationToken)
            : null;

    public async Task<BudgetResult> UpdateAsync(
        Guid workspaceId, Guid budgetId, Guid userId, string? description, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(workspaceId, userId, cancellationToken)
            || await budgets.FindBudgetAsync(workspaceId, budgetId, cancellationToken) is not { } budget)
            return BudgetResult.NotFound;
        if (budget.IsArchived) return BudgetResult.Conflict(BudgetResult.ArchivedMessage);
        if (!FinancialBudget.TryNormalizeDescription(description, out var normalized, out var error))
            return BudgetResult.Invalid(new Dictionary<string, string[]> { ["description"] = [error!] });
        budget.Update(normalized, userId, timeProvider.GetUtcNow());
        await budgets.TrySaveChangesAsync(cancellationToken);
        return BudgetResult.Ok((await budgets.GetAsync(workspaceId, budgetId, cancellationToken))!);
    }

    public async Task<BudgetResult> ArchiveAsync(
        Guid workspaceId, Guid budgetId, Guid userId, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(workspaceId, userId, cancellationToken)
            || await budgets.FindBudgetAsync(workspaceId, budgetId, cancellationToken) is not { } budget)
            return BudgetResult.NotFound;
        budget.Archive(userId, timeProvider.GetUtcNow());
        await budgets.TrySaveChangesAsync(cancellationToken);
        return BudgetResult.Ok((await budgets.GetAsync(workspaceId, budgetId, cancellationToken))!);
    }

    public async Task<BudgetResult> AddTargetAsync(
        Guid workspaceId, Guid budgetId, Guid userId, string? name, decimal? amount,
        string? groupingType, Guid? categoryId, CancellationToken cancellationToken, string? note = null)
    {
        if (!await IsParticipantAsync(workspaceId, userId, cancellationToken)
            || await budgets.FindBudgetAsync(workspaceId, budgetId, cancellationToken) is not { } budget)
            return BudgetResult.NotFound;
        if (budget.IsArchived) return BudgetResult.Conflict(BudgetResult.ArchivedMessage);
        var errors = ValidateTarget(name, amount, groupingType, categoryId, out var normalized);
        if (errors.Count > 0) return BudgetResult.Invalid(errors);
        var category = await budgets.GetCategoryAsync(workspaceId, categoryId!.Value, cancellationToken);
        if (category is null) return BudgetResult.NotFound;
        if (!category.IsExpense)
            return BudgetResult.Invalid(new Dictionary<string, string[]> { ["categoryId"] = ["A meta deve referenciar uma categoria de despesa."] });
        if (await budgets.TargetExistsAsync(budgetId, categoryId.Value, cancellationToken))
            return BudgetResult.Conflict(BudgetResult.DuplicateTargetMessage);
        if (await budgets.RelatedTargetExistsAsync(budgetId, categoryId.Value, cancellationToken))
            return BudgetResult.Conflict(BudgetResult.OverlappingTargetMessage);
        var now = timeProvider.GetUtcNow();
        var target = FinancialBudgetTarget.Create(
            workspaceId, budgetId, normalized, amount!.Value, categoryId.Value, userId, now);
        // O histórico (T10.2) é gravado junto com a meta; se a gravação falhar por duplicidade, nada é mantido.
        history.Stage(FinancialPlanHistoryEntry.Create(workspaceId, budget.Competence, PlanHistorySubject.CategoryLimit, target.Id, PlanHistoryAction.Created,
            normalized, null, amount.Value, note, userId, now));
        return await budgets.TryAddTargetAsync(target, cancellationToken)
            ? BudgetResult.Ok((await budgets.GetAsync(workspaceId, budgetId, cancellationToken))!)
            : BudgetResult.Conflict(BudgetResult.DuplicateTargetMessage);
    }

    public async Task<BudgetResult> UpdateTargetAsync(
        Guid workspaceId, Guid budgetId, Guid targetId, Guid userId, string? name, decimal? amount,
        CancellationToken cancellationToken, string? note = null)
    {
        if (!await IsParticipantAsync(workspaceId, userId, cancellationToken)
            || await budgets.FindBudgetAsync(workspaceId, budgetId, cancellationToken) is not { } budget
            || await budgets.FindTargetAsync(budgetId, targetId, cancellationToken) is not { } target)
            return BudgetResult.NotFound;
        if (budget.IsArchived || target.IsArchived) return BudgetResult.Conflict(BudgetResult.ArchivedMessage);
        var errors = ValidateTarget(name, amount, nameof(BudgetTargetGroupingType.Category), target.CategoryId, out var normalized);
        if (errors.Count > 0) return BudgetResult.Invalid(errors);
        var now = timeProvider.GetUtcNow();
        var previous = target.PlannedAmount;
        target.Update(normalized, amount!.Value, userId, now);
        if (previous != amount.Value)
            history.Stage(FinancialPlanHistoryEntry.Create(workspaceId, budget.Competence, PlanHistorySubject.CategoryLimit, target.Id, PlanHistoryAction.Revised,
                normalized, previous, amount.Value, note, userId, now));
        await budgets.TrySaveChangesAsync(cancellationToken);
        return BudgetResult.Ok((await budgets.GetAsync(workspaceId, budgetId, cancellationToken))!);
    }

    public async Task<BudgetResult> ArchiveTargetAsync(
        Guid workspaceId, Guid budgetId, Guid targetId, Guid userId, CancellationToken cancellationToken, string? note = null)
    {
        if (!await IsParticipantAsync(workspaceId, userId, cancellationToken)
            || await budgets.FindBudgetAsync(workspaceId, budgetId, cancellationToken) is not { } budget
            || await budgets.FindTargetAsync(budgetId, targetId, cancellationToken) is not { } target)
            return BudgetResult.NotFound;
        if (budget.IsArchived) return BudgetResult.Conflict(BudgetResult.ArchivedMessage);
        var now = timeProvider.GetUtcNow();
        if (!target.IsArchived)
            history.Stage(FinancialPlanHistoryEntry.Create(workspaceId, budget.Competence, PlanHistorySubject.CategoryLimit, target.Id, PlanHistoryAction.Removed,
                target.Name, target.PlannedAmount, null, note, userId, now));
        target.Archive(userId, now);
        await budgets.TrySaveChangesAsync(cancellationToken);
        return BudgetResult.Ok((await budgets.GetAsync(workspaceId, budgetId, cancellationToken))!);
    }

    /// <summary>Reativa o limite arquivado de uma categoria com um novo valor (T10.2). A categoria só tem uma meta por orçamento, então o limite removido volta no mesmo registro.</summary>
    public async Task<BudgetResult> RestoreTargetAsync(
        Guid workspaceId, Guid budgetId, Guid targetId, Guid userId, decimal? amount, CancellationToken cancellationToken, string? note = null)
    {
        if (!await IsParticipantAsync(workspaceId, userId, cancellationToken)
            || await budgets.FindBudgetAsync(workspaceId, budgetId, cancellationToken) is not { } budget
            || await budgets.FindTargetAsync(budgetId, targetId, cancellationToken) is not { } target)
            return BudgetResult.NotFound;
        if (budget.IsArchived) return BudgetResult.Conflict(BudgetResult.ArchivedMessage);
        if (await budgets.RelatedTargetExistsAsync(budgetId, target.CategoryId, cancellationToken)) return BudgetResult.Conflict(BudgetResult.OverlappingTargetMessage);
        var errors = ValidateTarget(target.Name, amount, nameof(BudgetTargetGroupingType.Category), target.CategoryId, out var normalized);
        if (errors.Count > 0) return BudgetResult.Invalid(errors);
        var now = timeProvider.GetUtcNow();
        target.Restore(normalized, amount!.Value, userId, now);
        history.Stage(FinancialPlanHistoryEntry.Create(workspaceId, budget.Competence, PlanHistorySubject.CategoryLimit, target.Id, PlanHistoryAction.Restored,
            normalized, null, amount.Value, note, userId, now));
        await budgets.TrySaveChangesAsync(cancellationToken);
        return BudgetResult.Ok((await budgets.GetAsync(workspaceId, budgetId, cancellationToken))!);
    }

    public static bool TryParseCompetence(string value, out DateOnly competence)
    {
        competence = default;
        return value.Length == 7 && DateOnly.TryParseExact(
            $"{value}-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out competence);
    }

    private static Dictionary<string, string[]> ValidateBudget(
        string? competence, string? description, out DateOnly month, out string? normalizedDescription)
    {
        var errors = new Dictionary<string, string[]>();
        month = default;
        if (competence is null || !TryParseCompetence(competence, out month))
            errors["competence"] = ["Informe uma competência válida no formato yyyy-MM."];
        if (!FinancialBudget.TryNormalizeDescription(description, out normalizedDescription, out var descriptionError))
            errors["description"] = [descriptionError!];
        return errors;
    }

    private static Dictionary<string, string[]> ValidateTarget(
        string? name, decimal? amount, string? groupingType, Guid? categoryId, out string normalized)
    {
        var errors = new Dictionary<string, string[]>();
        if (!FinancialBudgetTarget.TryNormalizeName(name, out normalized, out var nameError)) errors["name"] = [nameError!];
        if (amount is null) errors["plannedAmount"] = ["Informe o valor planejado."];
        else if (amount < 0m) errors["plannedAmount"] = ["O valor planejado não pode ser negativo."];
        else if (Money.Validate(amount.Value, "O valor planejado") is { } amountError) errors["plannedAmount"] = [amountError];
        if (groupingType != nameof(BudgetTargetGroupingType.Category))
            errors["groupingType"] = ["Agrupamento inválido. Use Category."];
        if (categoryId is null || categoryId == Guid.Empty) errors["categoryId"] = ["Informe a categoria de despesa."];
        return errors;
    }

    private async Task<bool> IsParticipantAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken) =>
        await workspaces.GetOwnerRoleAsync(workspaceId, userId, cancellationToken) is not null;
}
