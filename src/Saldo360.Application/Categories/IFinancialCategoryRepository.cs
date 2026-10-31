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

    /// <summary>
    /// O que usa a categoria (e, numa principal, as suas subcategorias): quantidade de registros por origem e as configurações ativas dependentes
    /// (recorrências ativas ou pausadas). Nulo se a categoria não for do orçamento.
    /// </summary>
    Task<CategoryImpactDto?> GetImpactAsync(Guid workspaceId, Guid categoryId, CancellationToken cancellationToken);

    /// <summary>
    /// Exclui fisicamente a categoria e as suas subcategorias, numa transação e somente se nada as referenciar. False, sem apagar nada, se houver
    /// vínculos (verificados de novo dentro da transação; as chaves estrangeiras do banco são a garantia final). Nunca apaga registros financeiros.
    /// </summary>
    Task<bool> TryDeleteAsync(Guid workspaceId, Guid categoryId, CancellationToken cancellationToken);

    /// <summary>
    /// Adiciona ao orçamento o que falta do catálogo, numa transação serializada por orçamento (aplicações concorrentes ou repetidas não duplicam).
    /// Devolve o plano efetivamente aplicado. Não altera nem reativa categorias existentes.
    /// </summary>
    Task<DefaultCategoriesPlan> ApplyDefaultCategoriesAsync(
        Guid workspaceId, Guid userId, IDefaultCategoryCatalog catalog, DateTimeOffset now, CancellationToken cancellationToken);
}
