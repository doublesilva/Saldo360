using Saldo360.Domain.Accounts;
using Saldo360.Domain.Common;

namespace Saldo360.Domain.Categories;

/// <summary>
/// Categoria de receita ou despesa da família, sem subcategorias. Serve a contas PF e PJ: o escopo vem da
/// conta do lançamento, não da categoria. O nome é único por família e tipo, sem diferenciar maiúsculas de
/// minúsculas e contando as inativas. O tipo não muda depois da criação, e não há exclusão física (só desativação).
/// </summary>
public sealed class FinancialCategory : Entity
{
    public const int NameMaxLength = 100;

    private FinancialCategory()
    {
        Name = string.Empty;
    }

    public Guid WorkspaceId { get; private init; }

    public string Name { get; private set; }

    public CategoryType Type { get; private init; }

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public Guid CreatedByUserId { get; private init; }

    /// <param name="name">Já normalizado por <see cref="TryNormalizeName"/>.</param>
    public static FinancialCategory Create(Guid workspaceId, string name, CategoryType type, Guid createdByUserId, DateTimeOffset now) =>
        new()
        {
            WorkspaceId = workspaceId,
            Name = name,
            Type = type,
            IsActive = true,
            CreatedAt = now,
            CreatedByUserId = createdByUserId,
        };

    /// <summary>Renomeia e ativa/desativa. Família, tipo, criador e data de criação não mudam.</summary>
    /// <param name="name">Já normalizado por <see cref="TryNormalizeName"/>.</param>
    public void Update(string name, bool isActive)
    {
        Name = name;
        IsActive = isActive;
    }

    /// <summary>Remove espaços nas pontas; obrigatório e com até 100 caracteres.</summary>
    public static bool TryNormalizeName(string? name, out string normalized, out string? error)
    {
        normalized = name?.Trim() ?? string.Empty;
        error = normalized.Length switch
        {
            0 => "Informe o nome da categoria.",
            > NameMaxLength => $"O nome deve ter no máximo {NameMaxLength} caracteres.",
            _ => null,
        };
        return error is null;
    }
}

/// <summary>Receita ou despesa. Persistido pelo nome.</summary>
public enum CategoryType
{
    Income,
    Expense,
}

/// <summary>Conversão estrita: só "Income" ou "Expense", exatamente.</summary>
public static class CategoryTypes
{
    public static IReadOnlyList<string> Names { get; } = Enum.GetNames<CategoryType>();

    public static bool TryParse(string? value, out CategoryType type) => StrictEnum.TryParse(value, out type);
}
