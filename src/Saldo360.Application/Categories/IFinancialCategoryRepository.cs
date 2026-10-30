using Saldo360.Domain.Categories;

namespace Saldo360.Application.Categories;

public interface IFinancialCategoryRepository
{
    /// <summary>Grava a categoria; false se o banco recusar por nome duplicado (família + tipo).</summary>
    Task<bool> TryAddAsync(FinancialCategory category, CancellationToken cancellationToken);

    /// <summary>A categoria, se pertencer à família (rastreada para atualização).</summary>
    Task<FinancialCategory?> FindAsync(Guid workspaceId, Guid categoryId, CancellationToken cancellationToken);

    /// <summary>Salva as alterações rastreadas; false se o banco recusar por nome duplicado.</summary>
    Task<bool> TrySaveChangesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Se já existe outra categoria (ativa ou inativa) com o mesmo nome no mesmo nível, sem diferenciar maiúsculas de minúsculas: entre as
    /// principais, por tipo; entre as subcategorias, dentro da mesma principal. Verificação prévia: a garantia final são os índices únicos do banco.
    /// </summary>
    Task<bool> NameExistsAsync(
        Guid workspaceId, CategoryType type, Guid? parentCategoryId, string name, Guid? exceptCategoryId, CancellationToken cancellationToken);

    /// <summary>Ativas primeiro; depois receitas antes de despesas; depois por nome.</summary>
    Task<IReadOnlyList<FinancialCategoryDto>> ListAsync(Guid workspaceId, bool includeInactive, CancellationToken cancellationToken);

    Task<FinancialCategoryDto?> GetAsync(Guid workspaceId, Guid categoryId, CancellationToken cancellationToken);
}
