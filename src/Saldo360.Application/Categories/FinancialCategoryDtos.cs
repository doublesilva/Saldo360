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
}

public sealed record CategoryResult(CategoryAccess Access, FinancialCategoryDto? Category, IDictionary<string, string[]>? Errors)
{
    public const string DuplicateMessage = "Já existe uma categoria com esse nome neste nível (ativa ou inativa): categorias principais são únicas por tipo e subcategorias, dentro da principal.";

    public static CategoryResult NotFound { get; } = new(CategoryAccess.NotFound, null, null);

    public static CategoryResult Conflict { get; } = new(CategoryAccess.Conflict, null, null);

    public static CategoryResult Ok(FinancialCategoryDto category) => new(CategoryAccess.Allowed, category, null);

    public static CategoryResult Invalid(IDictionary<string, string[]> errors) => new(CategoryAccess.Invalid, null, errors);
}
