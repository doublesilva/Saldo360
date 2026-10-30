using Saldo360.Application.Workspaces;
using Saldo360.Domain.Categories;

namespace Saldo360.Application.Categories;

/// <summary>
/// Categorias e subcategorias de receita e despesa (no máximo dois níveis). O dono do orçamento tem acesso; o orçamento vem da rota e o criador, da
/// sessão. Nome duplicado no mesmo nível (principal: por tipo; subcategoria: dentro da principal, sem diferenciar caixa) dá 409, inclusive em corrida.
/// Uma subcategoria herda o tipo da principal, que precisa ser do mesmo orçamento, estar ativa e não ser subcategoria.
/// </summary>
public sealed class FinancialCategoryService(
    IFinancialWorkspaceRepository workspaces,
    IFinancialCategoryRepository categories,
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

        category.Update(normalized, isActive!.Value);
        return await categories.TrySaveChangesAsync(cancellationToken)
            ? CategoryResult.Ok((await categories.GetAsync(workspaceId, category.Id, cancellationToken))!)
            : CategoryResult.Conflict;
    }

    private async Task<bool> IsParticipantAsync(Guid workspaceId, Guid userId, CancellationToken cancellationToken) =>
        await workspaces.GetOwnerRoleAsync(workspaceId, userId, cancellationToken) is not null;
}
