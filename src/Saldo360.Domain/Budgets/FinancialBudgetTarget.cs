using Saldo360.Domain.Common;
using Saldo360.Domain.Categories;

namespace Saldo360.Domain.Budgets;

/// <summary>
/// Meta manual de um orçamento mensal; movimentações nunca alteram seu valor. Três tipos: por categoria (inclui as subcategorias de uma principal),
/// despesas fixas (toda despesa classificada como fixa, de qualquer categoria e meio de pagamento) e todos os cartões (gastos de todos os cartões
/// somados). Fixas e cartões têm no máximo uma meta por orçamento e não têm categoria. Perspectivas diferentes podem coexistir e nunca se somam.
/// </summary>
public sealed class FinancialBudgetTarget : Entity
{
    public const int NameMaxLength = 100;

    private FinancialBudgetTarget() { Name = string.Empty; }

    public Guid WorkspaceId { get; private init; }
    public Guid BudgetId { get; private init; }
    public string Name { get; private set; }
    public decimal PlannedAmount { get; private set; }
    public BudgetTargetGroupingType GroupingType { get; private init; }
    /// <summary>Preenchida só em meta por categoria.</summary>
    public Guid? CategoryId { get; private init; }
    public CategoryType? CategoryType { get; private init; }
    public bool IsArchived { get; private set; }
    public DateTimeOffset CreatedAt { get; private init; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid CreatedByUserId { get; private init; }
    public Guid UpdatedByUserId { get; private set; }
    public DateTimeOffset? ArchivedAt { get; private set; }
    public Guid? ArchivedByUserId { get; private set; }

    public static FinancialBudgetTarget Create(
        Guid workspaceId, Guid budgetId, string name, decimal amount, Guid categoryId, Guid userId, DateTimeOffset now) => new()
        {
            WorkspaceId = workspaceId,
            BudgetId = budgetId,
            Name = name,
            PlannedAmount = amount,
            GroupingType = BudgetTargetGroupingType.Category,
            CategoryId = categoryId,
            CategoryType = Categories.CategoryType.Expense,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedByUserId = userId,
            UpdatedByUserId = userId,
        };

    /// <summary>Meta consolidada sem categoria: <see cref="BudgetTargetGroupingType.FixedExpenses"/> ou <see cref="BudgetTargetGroupingType.AllCards"/>.</summary>
    public static FinancialBudgetTarget CreateAggregate(
        Guid workspaceId, Guid budgetId, string name, decimal amount, BudgetTargetGroupingType groupingType, Guid userId, DateTimeOffset now)
    {
        if (groupingType == BudgetTargetGroupingType.Category) throw new ArgumentException("Meta por categoria exige a categoria.", nameof(groupingType));
        return new()
        {
            WorkspaceId = workspaceId,
            BudgetId = budgetId,
            Name = name,
            PlannedAmount = amount,
            GroupingType = groupingType,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedByUserId = userId,
            UpdatedByUserId = userId,
        };
    }

    public void Update(string name, decimal amount, Guid userId, DateTimeOffset now)
    {
        Name = name;
        PlannedAmount = amount;
        UpdatedAt = now;
        UpdatedByUserId = userId;
    }

    public void Archive(Guid userId, DateTimeOffset now)
    {
        if (IsArchived) return;
        IsArchived = true;
        ArchivedAt = now;
        ArchivedByUserId = userId;
        UpdatedAt = now;
        UpdatedByUserId = userId;
    }

    /// <summary>Reativa uma meta arquivada com um novo valor (a categoria só pode ter uma meta por orçamento).</summary>
    public void Restore(string name, decimal amount, Guid userId, DateTimeOffset now)
    {
        Name = name;
        PlannedAmount = amount;
        IsArchived = false;
        ArchivedAt = null;
        ArchivedByUserId = null;
        UpdatedAt = now;
        UpdatedByUserId = userId;
    }

    public static bool TryNormalizeName(string? value, out string normalized, out string? error)
    {
        normalized = value?.Trim() ?? string.Empty;
        error = normalized.Length switch
        {
            0 => "Informe o nome da meta.",
            > NameMaxLength => $"O nome deve ter no máximo {NameMaxLength} caracteres.",
            _ => null,
        };
        return error is null;
    }
}

public enum BudgetTargetGroupingType
{
    Category,
    FixedExpenses,
    AllCards,
}
