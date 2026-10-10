using Microsoft.EntityFrameworkCore;
using Npgsql;
using Saldo360.Application.Categories;
using Saldo360.Domain.Categories;
using Saldo360.Infrastructure.Persistence;
using Saldo360.Infrastructure.Persistence.Configurations;

namespace Saldo360.Infrastructure.Categories;

internal sealed class FinancialCategoryRepository(Saldo360DbContext db) : IFinancialCategoryRepository
{
    public async Task<bool> TryAddAsync(FinancialCategory category, CancellationToken cancellationToken)
    {
        db.FinancialCategories.Add(category);
        return await TrySaveChangesAsync(cancellationToken);
    }

    public Task<FinancialCategory?> FindAsync(Guid familyId, Guid categoryId, CancellationToken cancellationToken) =>
        db.FinancialCategories.SingleOrDefaultAsync(c => c.Id == categoryId && c.FamilyId == familyId, cancellationToken);

    public async Task<bool> TrySaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (IsDuplicateName(exception))
        {
            // Outra requisição gravou o mesmo nome entre a verificação e o INSERT/UPDATE.
            db.ChangeTracker.Clear();
            return false;
        }
    }

    // A coluna name usa colação ICU que ignora a caixa: a igualdade abaixo já é case-insensitive no banco.
    public Task<bool> NameExistsAsync(
        Guid familyId, CategoryType type, string name, Guid? exceptCategoryId, CancellationToken cancellationToken) =>
        db.FinancialCategories.AnyAsync(
            c => c.FamilyId == familyId && c.Type == type && c.Name == name && c.Id != exceptCategoryId,
            cancellationToken);

    public async Task<IReadOnlyList<FinancialCategoryDto>> ListAsync(
        Guid familyId, bool includeInactive, CancellationToken cancellationToken) =>
        await Project(db.FinancialCategories
                .Where(c => c.FamilyId == familyId && (includeInactive || c.IsActive))
                .OrderByDescending(c => c.IsActive)
                .ThenBy(c => c.Type == CategoryType.Income ? 0 : 1) // receitas antes de despesas
                .ThenBy(c => c.Name))
            .ToListAsync(cancellationToken);

    public Task<FinancialCategoryDto?> GetAsync(Guid familyId, Guid categoryId, CancellationToken cancellationToken) =>
        Project(db.FinancialCategories.Where(c => c.FamilyId == familyId && c.Id == categoryId))
            .SingleOrDefaultAsync(cancellationToken);

    private static IQueryable<FinancialCategoryDto> Project(IQueryable<FinancialCategory> categories) =>
        categories.AsNoTracking().Select(c => new FinancialCategoryDto(c.Id, c.Name, c.Type, c.IsActive, c.CreatedAt));

    private static bool IsDuplicateName(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: FinancialCategoryConfiguration.UniqueNameIndex,
        };
}
