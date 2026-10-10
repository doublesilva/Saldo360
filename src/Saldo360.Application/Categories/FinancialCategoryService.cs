using Saldo360.Application.Families;
using Saldo360.Domain.Categories;

namespace Saldo360.Application.Categories;

/// <summary>
/// Categorias de receita e despesa da família. Owner e Member têm o mesmo acesso. Família vem da rota,
/// criador vem da sessão. Nome duplicado (família + tipo, sem diferenciar caixa) dá 409, inclusive em corrida.
/// </summary>
public sealed class FinancialCategoryService(
    IFamilyRepository families,
    IFinancialCategoryRepository categories,
    TimeProvider timeProvider)
{
    private const string NameField = "name";

    public async Task<CategoryResult> CreateAsync(
        Guid familyId, Guid userId, string? name, string? type, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(familyId, userId, cancellationToken))
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

        if (await categories.NameExistsAsync(familyId, parsedType, normalized, null, cancellationToken))
        {
            return CategoryResult.Conflict;
        }

        var category = FinancialCategory.Create(familyId, normalized, parsedType, userId, timeProvider.GetUtcNow());
        return await categories.TryAddAsync(category, cancellationToken)
            ? CategoryResult.Ok((await categories.GetAsync(familyId, category.Id, cancellationToken))!)
            : CategoryResult.Conflict;
    }

    public async Task<IReadOnlyList<FinancialCategoryDto>?> ListAsync(
        Guid familyId, Guid userId, bool includeInactive, CancellationToken cancellationToken) =>
        await IsParticipantAsync(familyId, userId, cancellationToken)
            ? await categories.ListAsync(familyId, includeInactive, cancellationToken)
            : null;

    public async Task<FinancialCategoryDto?> GetAsync(Guid familyId, Guid categoryId, Guid userId, CancellationToken cancellationToken) =>
        await IsParticipantAsync(familyId, userId, cancellationToken)
            ? await categories.GetAsync(familyId, categoryId, cancellationToken)
            : null;

    /// <summary>Renomeia e ativa/desativa. O tipo não é editável.</summary>
    public async Task<CategoryResult> UpdateAsync(
        Guid familyId, Guid categoryId, Guid userId, string? name, bool? isActive, CancellationToken cancellationToken)
    {
        if (!await IsParticipantAsync(familyId, userId, cancellationToken)
            || await categories.FindAsync(familyId, categoryId, cancellationToken) is not { } category)
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

        if (await categories.NameExistsAsync(familyId, category.Type, normalized, category.Id, cancellationToken))
        {
            return CategoryResult.Conflict;
        }

        category.Update(normalized, isActive!.Value);
        return await categories.TrySaveChangesAsync(cancellationToken)
            ? CategoryResult.Ok((await categories.GetAsync(familyId, category.Id, cancellationToken))!)
            : CategoryResult.Conflict;
    }

    private async Task<bool> IsParticipantAsync(Guid familyId, Guid userId, CancellationToken cancellationToken) =>
        await families.GetMemberRoleAsync(familyId, userId, cancellationToken) is not null;
}
