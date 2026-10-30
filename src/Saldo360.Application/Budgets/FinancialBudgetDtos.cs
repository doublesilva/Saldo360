using Saldo360.Domain.Budgets;

namespace Saldo360.Application.Budgets;

public sealed record FinancialBudgetCategoryDto(Guid Id, string Name, bool IsActive, string? ParentName = null)
{
    public string Path => ParentName is null ? Name : $"{ParentName} / {Name}";
}

public sealed record FinancialBudgetTargetDto(
    Guid Id,
    string Name,
    decimal PlannedAmount,
    BudgetTargetGroupingType GroupingType,
    FinancialBudgetCategoryDto? Category,
    bool IsArchived,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt);

public sealed record FinancialBudgetDto(
    Guid Id,
    DateOnly Competence,
    string? Description,
    bool IsArchived,
    decimal TotalPlannedAmount,
    IReadOnlyList<FinancialBudgetTargetDto> Targets,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt);

public enum BudgetAccess { Allowed, NotFound, Invalid, Conflict }

public sealed record BudgetResult(
    BudgetAccess Access,
    FinancialBudgetDto? Budget,
    IDictionary<string, string[]>? Errors,
    string? Message = null)
{
    public const string DuplicateBudgetMessage = "Já existe um orçamento para esta família e competência.";
    public const string DuplicateTargetMessage = "Já existe uma meta para esta categoria neste orçamento.";
    public const string DuplicateFixedTargetMessage = "Já existe uma meta de despesas fixas neste orçamento: edite o limite em vez de criar outra.";
    public const string DuplicateCardsTargetMessage = "Já existe a meta de todos os cartões neste orçamento: edite o limite em vez de criar outra.";
    public const string OverlappingTargetMessage = "Já existe meta para a categoria principal ou para uma subcategoria desta: use um só nível para o mesmo gasto não ser contado duas vezes.";
    public const string ArchivedMessage = "Orçamento ou meta arquivada não pode ser alterada.";
    public static BudgetResult NotFound { get; } = new(BudgetAccess.NotFound, null, null);
    public static BudgetResult Invalid(IDictionary<string, string[]> errors) => new(BudgetAccess.Invalid, null, errors);
    public static BudgetResult Conflict(string message) => new(BudgetAccess.Conflict, null, null, message);
    public static BudgetResult Ok(FinancialBudgetDto budget) => new(BudgetAccess.Allowed, budget, null);
}
