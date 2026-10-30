using Saldo360.Domain.Accounts;
using Saldo360.Domain.Common;

namespace Saldo360.Domain.Categories;

/// <summary>
/// Categoria de receita ou despesa do orçamento, com no máximo dois níveis (R02): categoria principal e subcategoria opcional. Uma subcategoria é uma
/// linha desta mesma tabela com <see cref="ParentCategoryId"/>; o lançamento aponta para UMA categoria (a principal ou a subcategoria), então a
/// classificação nunca diverge. A subcategoria tem o mesmo orçamento e o mesmo tipo da principal e não tem filhas.
/// Serve a contas PF e PJ: o escopo vem da conta do lançamento, não da categoria. O nome da principal é único por orçamento e tipo, e o da
/// subcategoria é único dentro da principal, sem diferenciar maiúsculas de minúsculas e contando as inativas. Tipo e principal não mudam depois
/// da criação, e não há exclusão física (só desativação).
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

    /// <summary>Categoria principal desta subcategoria; nulo = esta é uma categoria principal. Imutável.</summary>
    public Guid? ParentCategoryId { get; private init; }

    public bool IsSubcategory => ParentCategoryId is not null;

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

    /// <summary>Subcategoria de <paramref name="parent"/>: herda orçamento e tipo. A aplicação garante que a principal é ativa e não é, ela mesma, uma subcategoria.</summary>
    public static FinancialCategory CreateSubcategory(FinancialCategory parent, string name, Guid createdByUserId, DateTimeOffset now)
    {
        if (parent.IsSubcategory) throw new InvalidOperationException("Subcategoria não pode ter subcategorias.");
        return new()
        {
            WorkspaceId = parent.WorkspaceId,
            Name = name,
            Type = parent.Type,
            ParentCategoryId = parent.Id,
            IsActive = true,
            CreatedAt = now,
            CreatedByUserId = createdByUserId,
        };
    }

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
