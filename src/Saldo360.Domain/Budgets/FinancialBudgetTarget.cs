using Saldo360.Domain.Common;
using Saldo360.Domain.Categories;

namespace Saldo360.Domain.Budgets;

/// <summary>Meta manual de despesa por categoria; movimentações nunca alteram seu valor.</summary>
public sealed class FinancialBudgetTarget : Entity
{
    public const int NameMaxLength = 100;

    private FinancialBudgetTarget() { Name = string.Empty; }

    public Guid FamilyId { get; private init; }
    public Guid BudgetId { get; private init; }
    public string Name { get; private set; }
    public decimal PlannedAmount { get; private set; }
    public BudgetTargetGroupingType GroupingType { get; private init; }
    public Guid CategoryId { get; private init; }
    public CategoryType CategoryType { get; private init; }
    public bool IsArchived { get; private set; }
    public DateTimeOffset CreatedAt { get; private init; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid CreatedByUserId { get; private init; }
    public Guid UpdatedByUserId { get; private set; }
    public DateTimeOffset? ArchivedAt { get; private set; }
    public Guid? ArchivedByUserId { get; private set; }

    public static FinancialBudgetTarget Create(
        Guid familyId, Guid budgetId, string name, decimal amount, Guid categoryId, Guid userId, DateTimeOffset now) => new()
        {
            FamilyId = familyId,
            BudgetId = budgetId,
            Name = name,
            PlannedAmount = amount,
            GroupingType = BudgetTargetGroupingType.Category,
            CategoryId = categoryId,
            CategoryType = CategoryType.Expense,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedByUserId = userId,
            UpdatedByUserId = userId,
        };

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
}
