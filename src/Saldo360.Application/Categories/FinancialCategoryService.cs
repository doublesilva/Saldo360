using Saldo360.Application.Workspaces;
using Saldo360.Domain.Categories;

namespace Saldo360.Application.Categories;

/// <summary>
/// Categorias e subcategorias de receita e despesa (no máximo dois níveis). O dono do orçamento tem acesso; o orçamento vem da rota e o criador, da
/// sessão. Nome duplicado no mesmo nível (principal: por tipo; subcategoria: dentro da principal, sem diferenciar caixa) dá 409, inclusive em corrida.
/// Uma subcategoria herda o tipo da principal, que precisa ser do mesmo orçamento, estar ativa e não ser subcategoria.
/// Excluir só é possível sem vínculos (senão, 409 com o impacto e a alternativa de inativar); inativar exige antes resolver as configurações ativas
/// que dependem da categoria. O catálogo padrão pode ser acrescentado a um orçamento existente por ação explícita, sempre sem alterar o que já existe.
/// </summary>
public sealed class FinancialCategoryService(
    IFinancialWorkspaceRepository workspaces,
    IFinancialCategoryRepository categories,
    IDefaultCategoryCatalog catalog,
    TimeProvider timeProvider)
{
    private const string NameField = "name";

    public async Task<CategoryResult> CreateAsync(
        Guid workspaceId, Guid userId, string? name, string? type, Guid? parentCategoryId, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(workspaceId, userId, cancellationToken))
        {
            return CategoryResult.NotFound;
        }

        var errors = new Dictionary<string, string[]>();
        if (!FinancialCategory.TryNormalizeName(name, out var normalized, out var nameError))
        {
            errors[NameField] = [nameError!];
        }

        FinancialCategory? parent = null;
        var parsedType = CategoryType.Expense;
        if (parentCategoryId is { } parentId)
        {
            // O parentId vem do cliente: só vale se for deste orçamento (outro orçamento é igual a inexistente).
            parent = await categories.FindAsync(workspaceId, parentId, cancellationToken);
            if (parent is null)
            {
                errors["parentCategoryId"] = ["Categoria principal inválida."];
            }
            else if (parent.IsSubcategory)
            {
                errors["parentCategoryId"] = ["Subcategoria não pode ter subcategorias: escolha uma categoria principal."];
            }
            else if (!parent.IsActive)
            {
                errors["parentCategoryId"] = ["A categoria principal está inativa."];
            }
            else
            {
                parsedType = parent.Type;
                if (type is not null && (!CategoryTypes.TryParse(type, out var requested) || requested != parent.Type))
                {
                    errors["type"] = ["A subcategoria tem sempre o mesmo tipo da categoria principal."];
                }
            }
        }
        else if (!CategoryTypes.TryParse(type, out parsedType))
        {
            errors["type"] = ["Tipo inválido. Use Income ou Expense."];
        }

        if (errors.Count > 0)
        {
            return CategoryResult.Invalid(errors);
        }

        if (await categories.NameExistsAsync(workspaceId, parsedType, parent?.Id, normalized, null, cancellationToken))
        {
            return CategoryResult.Conflict;
        }

        var category = parent is null
            ? FinancialCategory.Create(workspaceId, normalized, parsedType, userId, timeProvider.GetUtcNow())
            : FinancialCategory.CreateSubcategory(parent, normalized, userId, timeProvider.GetUtcNow());
        return await categories.TryAddAsync(category, cancellationToken)
            ? CategoryResult.Ok((await categories.GetAsync(workspaceId, category.Id, cancellationToken))!)
            : CategoryResult.Conflict;
    }

    public async Task<IReadOnlyList<FinancialCategoryDto>?> ListAsync(
        Guid workspaceId, Guid userId, bool includeInactive, CancellationToken cancellationToken) =>
        await IsParticipantAsync(workspaceId, userId, cancellationToken)
            ? await categories.ListAsync(workspaceId, includeInactive, cancellationToken)
            : null;

    public async Task<FinancialCategoryDto?> GetAsync(Guid workspaceId, Guid categoryId, Guid userId, CancellationToken cancellationToken) =>
        await IsParticipantAsync(workspaceId, userId, cancellationToken)
            ? await categories.GetAsync(workspaceId, categoryId, cancellationToken)
            : null;

    /// <summary>
    /// Renomeia e ativa/desativa. O tipo e a categoria principal não são editáveis. Desativar uma principal torna as subcategorias indisponíveis para novas
    /// classificações (sem alterá-las); registros já classificados continuam legíveis.
    /// </summary>
    public async Task<CategoryResult> UpdateAsync(
        Guid workspaceId, Guid categoryId, Guid userId, string? name, bool? isActive, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(workspaceId, userId, cancellationToken)
            || await categories.FindAsync(workspaceId, categoryId, cancellationToken) is not { } category)
        {
            return CategoryResult.NotFound;
        }

        var errors = new Dictionary<string, string[]>();
        if (!FinancialCategory.TryNormalizeName(name, out var normalized, out var nameError))
        {
            errors[NameField] = [nameError!];
        }

        if (isActive is null)
        {
            errors["isActive"] = ["Informe se a categoria está ativa."];
        }

        if (errors.Count > 0)
        {
            return CategoryResult.Invalid(errors);
        }

        if (await categories.NameExistsAsync(workspaceId, category.Type, category.ParentCategoryId, normalized, category.Id, cancellationToken))
        {
            return CategoryResult.Conflict;
        }

        if (category.IsActive && !isActive!.Value
            && await categories.GetImpactAsync(workspaceId, category.Id, cancellationToken) is { CanDeactivate: false } impact)
        {
            return CategoryResult.Blocked(impact);
        }

        category.Update(normalized, isActive!.Value);
        return await categories.TrySaveChangesAsync(cancellationToken)
            ? CategoryResult.Ok((await categories.GetAsync(workspaceId, category.Id, cancellationToken))!)
            : CategoryResult.Conflict;
    }

    /// <summary>Efeito de excluir ou inativar: o que usa a categoria (e as suas subcategorias) e o que ainda depende dela. Nulo se não for do orçamento.</summary>
    public async Task<CategoryImpactDto?> GetImpactAsync(Guid workspaceId, Guid categoryId, Guid userId, CancellationToken cancellationToken) =>
        await IsParticipantAsync(workspaceId, userId, cancellationToken)
            ? await categories.GetImpactAsync(workspaceId, categoryId, cancellationToken)
            : null;

    /// <summary>
    /// Exclui a categoria (e as subcategorias dela) somente se nada a referenciar. Com subcategorias, exige <paramref name="confirmSubcategories"/>, que
    /// o cliente só envia depois de mostrar o impacto. Com vínculos nada é apagado e a resposta traz o impacto; registros financeiros nunca são
    /// excluídos em cascata nem reclassificados.
    /// </summary>
    public async Task<CategoryDeleteResult> DeleteAsync(
        Guid workspaceId, Guid categoryId, Guid userId, bool confirmSubcategories, CancellationToken cancellationToken)
    {
        if (await GetImpactAsync(workspaceId, categoryId, userId, cancellationToken) is not { } impact)
        {
            return CategoryDeleteResult.NotFound;
        }

        if (!impact.CanDelete)
        {
            return new CategoryDeleteResult(CategoryDeleteOutcome.InUse, impact);
        }

        if (impact.SubcategoryCount > 0 && !confirmSubcategories)
        {
            return new CategoryDeleteResult(CategoryDeleteOutcome.ConfirmationRequired, impact);
        }

        if (await categories.TryDeleteAsync(workspaceId, categoryId, cancellationToken))
        {
            return new CategoryDeleteResult(CategoryDeleteOutcome.Deleted, impact);
        }

        // Vínculo surgido durante a exclusão (ou categoria removida por outra requisição): reavalia para responder com o impacto atual.
        return await GetImpactAsync(workspaceId, categoryId, userId, cancellationToken) is { } current
            ? new CategoryDeleteResult(CategoryDeleteOutcome.InUse, current)
            : CategoryDeleteResult.NotFound;
    }

    /// <summary>Pré-visualização de "Adicionar categorias padrão": o que seria criado e o que já existe (sem alterar nada).</summary>
    public async Task<DefaultCategoriesPreviewDto?> PreviewDefaultsAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken) =>
        await IsParticipantAsync(workspaceId, userId, cancellationToken)
            ? ToPreview(DefaultCategoryPlanner.Plan(catalog, await categories.ListAsync(workspaceId, includeInactive: true, cancellationToken)))
            : null;

    /// <summary>
    /// Acrescenta ao orçamento o que falta do catálogo. Idempotente: categorias equivalentes (tipo e nome normalizado; subcategorias dentro da mesma
    /// principal) são reutilizadas, nada é renomeado, reativado ou reclassificado, e repetir a ação não cria duplicatas.
    /// </summary>
    public async Task<DefaultCategoriesApplyDto?> ApplyDefaultsAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(workspaceId, userId, cancellationToken))
        {
            return null;
        }

        var plan = await categories.ApplyDefaultCategoriesAsync(workspaceId, userId, catalog, timeProvider.GetUtcNow(), cancellationToken);
        return new DefaultCategoriesApplyDto(plan.CategoriesToCreate, plan.SubcategoriesToCreate, ToPreview(plan));
    }

    private static DefaultCategoriesPreviewDto ToPreview(DefaultCategoriesPlan plan) =>
        new(plan.CatalogVersion, plan.CategoriesToCreate, plan.SubcategoriesToCreate, plan.Categories);

    private async Task<bool> IsParticipantAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken) =>
        await workspaces.GetOwnerRoleAsync(workspaceId, userId, cancellationToken) is not null;
}
