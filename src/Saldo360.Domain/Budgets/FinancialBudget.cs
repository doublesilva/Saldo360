using Saldo360.Domain.Common;

namespace Saldo360.Domain.Budgets;

/// <summary>Planejamento estável de uma família para uma competência mensal.</summary>
public sealed class FinancialBudget : Entity
{
    public const int DescriptionMaxLength = 200;

    private FinancialBudget() { }

    public Guid FamilyId { get; private init; }
    public DateOnly Competence { get; private init; }
    public string? Description { get; private set; }
    public bool IsArchived { get; private set; }
    public DateTimeOffset CreatedAt { get; private init; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid CreatedByUserId { get; private init; }
    public Guid UpdatedByUserId { get; private set; }
    public DateTimeOffset? ArchivedAt { get; private set; }
    public Guid? ArchivedByUserId { get; private set; }

    public static FinancialBudget Create(
        Guid familyId, DateOnly competence, string? description, Guid userId, DateTimeOffset now) => new()
        {
            FamilyId = familyId,
            Competence = competence,
            Description = description,
            CreatedAt = now,
            UpdatedAt = now,
            CreatedByUserId = userId,
            UpdatedByUserId = userId,
        };

    public void Update(string? description, Guid userId, DateTimeOffset now)
    {
        Description = description;
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

    public static bool TryNormalizeDescription(string? value, out string? normalized, out string? error)
    {
        normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        error = normalized is { Length: > DescriptionMaxLength }
            ? $"A descrição deve ter no máximo {DescriptionMaxLength} caracteres."
            : null;
        return error is null;
    }
}
