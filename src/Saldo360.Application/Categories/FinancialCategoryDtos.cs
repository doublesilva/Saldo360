using Saldo360.Domain.Categories;

namespace Saldo360.Application.Categories;

/// <summary>
/// Categoria como exibida ao dono do orçamento. Não expõe o criador. Subcategoria tem <see cref="ParentCategoryId"/> (e o nome da principal);
/// <see cref="IsAvailable"/> diz se ela pode receber NOVAS classificações: ativa e, no caso de subcategoria, com a principal também ativa.
/// A leitura de registros já classificados nunca depende disso.
/// </summary>
public sealed record FinancialCategoryDto(
    Guid Id,
    string Name,
    CategoryType Type,
    bool IsActive,
    DateTimeOffset CreatedAt,
    Guid? ParentCategoryId = null,
    string? ParentName = null,
    bool IsAvailable = true)
{
    /// <summary>"Categoria / Subcategoria" (ou só o nome, numa principal).</summary>
    public string Path => ParentName is null ? Name : $"{ParentName} / {Name}";
}

public enum CategoryAccess
{
    Allowed,

    /// <summary>Família ou categoria inexistente, ou usuário sem participação (404).</summary>
    NotFound,

    /// <summary>Dados inválidos (400).</summary>
    Invalid,

    /// <summary>Já existe categoria com o mesmo nome (e tipo, ou principal) no orçamento, inclusive inativa (409).</summary>
    Conflict,

    /// <summary>Inativação impedida por configurações ativas que dependem da categoria (409); o impacto as lista.</summary>
    Blocked,
}

public sealed record CategoryResult(
    CategoryAccess Access, FinancialCategoryDto? Category, IDictionary<string, string[]>? Errors, CategoryImpactDto? Impact = null)
{
    public static CategoryResult Blocked(CategoryImpactDto impact) => new(CategoryAccess.Blocked, null, null, impact);

    public const string DuplicateMessage = "Já existe uma categoria com esse nome neste nível (ativa ou inativa): categorias principais são únicas por tipo e subcategorias, dentro da principal.";

    public static CategoryResult NotFound { get; } = new(CategoryAccess.NotFound, null, null);

    public static CategoryResult Conflict { get; } = new(CategoryAccess.Conflict, null, null);

    public static CategoryResult Ok(FinancialCategoryDto category) => new(CategoryAccess.Allowed, category, null);

    public static CategoryResult Invalid(IDictionary<string, string[]> errors) => new(CategoryAccess.Invalid, null, errors);
}

/// <summary>Quantos registros de uma origem (ex.: "Lançamentos") usam a categoria ou alguma das suas subcategorias.</summary>
public sealed record CategoryReferenceDto(string Source, string Label, int Count);

/// <summary>Configuração ainda ativa que depende da categoria (hoje, recorrências ativas ou pausadas) e precisa ser resolvida antes de inativá-la.</summary>
public sealed record CategoryDependencyDto(string Kind, Guid Id, string Description);

/// <summary>
/// Efeito de excluir ou inativar uma categoria. Excluir só é possível sem vínculos (<see cref="CanDelete"/>); inativar sempre preserva o histórico,
/// mas exige resolver as dependências ativas (<see cref="CanDeactivate"/>). Para uma principal, os números incluem as suas subcategorias.
/// </summary>
public sealed record CategoryImpactDto(
    Guid CategoryId,
    string Name,
    bool IsSubcategory,
    int SubcategoryCount,
    IReadOnlyList<CategoryReferenceDto> References,
    IReadOnlyList<CategoryDependencyDto> ActiveDependencies)
{
    public int TotalReferences => References.Sum(r => r.Count);

    public bool CanDelete => TotalReferences == 0;

    public bool CanDeactivate => ActiveDependencies.Count == 0;
}

/// <summary>Pré-visualização da ação "Adicionar categorias padrão": o que será criado, reutilizado ou deixado como está.</summary>
public sealed record DefaultCategoriesPreviewDto(
    int CatalogVersion, int CategoriesToCreate, int SubcategoriesToCreate, IReadOnlyList<DefaultCategoryPlan> Categories);

public sealed record DefaultCategoriesApplyDto(int CategoriesCreated, int SubcategoriesCreated, DefaultCategoriesPreviewDto Applied);

public enum CategoryDeleteOutcome
{
    Deleted,

    /// <summary>Orçamento ou categoria inexistente, ou usuário sem participação (404).</summary>
    NotFound,

    /// <summary>Há registros vinculados (409): nada é apagado; o impacto indica o que usa a categoria e a alternativa é inativar.</summary>
    InUse,

    /// <summary>Categoria com subcategorias e sem confirmação explícita do impacto (409).</summary>
    ConfirmationRequired,
}

public sealed record CategoryDeleteResult(CategoryDeleteOutcome Outcome, CategoryImpactDto? Impact)
{
    public static CategoryDeleteResult NotFound { get; } = new(CategoryDeleteOutcome.NotFound, null);
}
