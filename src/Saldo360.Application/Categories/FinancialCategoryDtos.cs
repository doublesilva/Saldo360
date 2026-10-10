using Saldo360.Domain.Categories;

namespace Saldo360.Application.Categories;

/// <summary>Categoria como exibida aos integrantes da família. Não expõe o criador.</summary>
public sealed record FinancialCategoryDto(
    Guid Id,
    string Name,
    CategoryType Type,
    bool IsActive,
    DateTimeOffset CreatedAt);

public enum CategoryAccess
{
    Allowed,

    /// <summary>Família ou categoria inexistente, ou usuário sem participação (404).</summary>
    NotFound,

    /// <summary>Dados inválidos (400).</summary>
    Invalid,

    /// <summary>Já existe categoria com o mesmo nome e tipo na família, inclusive inativa (409).</summary>
    Conflict,
}

public sealed record CategoryResult(CategoryAccess Access, FinancialCategoryDto? Category, IDictionary<string, string[]>? Errors)
{
    public const string DuplicateMessage = "Já existe uma categoria com esse nome e tipo nesta família (ativa ou inativa).";

    public static CategoryResult NotFound { get; } = new(CategoryAccess.NotFound, null, null);

    public static CategoryResult Conflict { get; } = new(CategoryAccess.Conflict, null, null);

    public static CategoryResult Ok(FinancialCategoryDto category) => new(CategoryAccess.Allowed, category, null);

    public static CategoryResult Invalid(IDictionary<string, string[]> errors) => new(CategoryAccess.Invalid, null, errors);
}
