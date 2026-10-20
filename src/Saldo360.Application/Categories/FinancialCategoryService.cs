using Saldo360.Application.Workspaces;
using Saldo360.Domain.Categories;

namespace Saldo360.Application.Categories;

/// <summary>
/// Categorias de receita e despesa da família. Owner e Member têm o mesmo acesso. Família vem da rota,
/// criador vem da sessão. Nome duplicado (família + tipo, sem diferenciar caixa) dá 409, inclusive em corrida.
/// </summary>
public sealed class FinancialCategoryService(
    IFinancialWorkspaceRepository workspaces,
    IFinancialCategoryRepository categories,
    TimeProvider timeProvider)
{
    private const string NameField = "name";

    public async Task<CategoryResult> CreateAsync(
        Guid workspaceId, Guid userId, string? name, string? type, CancellationToken cancellationToken)
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

        if (!CategoryTypes.TryParse(type, out var parsedType))
        {
            errors["type"] = ["Tipo inválido. Use Income ou Expense."];
        }

        if (errors.Count > 0)
        {
            return CategoryResult.Invalid(errors);
        }

        if (await categories.NameExistsAsync(workspaceId, parsedType, normalized, null, cancellationToken))
        {
            return CategoryResult.Conflict;
        }

        var category = FinancialCategory.Create(workspaceId, normalized, parsedType, userId, timeProvider.GetUtcNow());
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

    /// <summary>Renomeia e ativa/desativa. O tipo não é editável.</summary>
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

        if (await categories.NameExistsAsync(workspaceId, category.Type, normalized, category.Id, cancellationToken))
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
