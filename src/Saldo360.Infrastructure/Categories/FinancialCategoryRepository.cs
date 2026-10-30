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

    public Task<FinancialCategory?> FindAsync(Guid workspaceId, Guid categoryId, CancellationToken cancellationToken) =>
        db.FinancialCategories.SingleOrDefaultAsync(c => c.Id == categoryId && c.WorkspaceId == workspaceId, cancellationToken);

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
        Guid workspaceId, CategoryType type, Guid? parentCategoryId, string name, Guid? exceptCategoryId, CancellationToken cancellationToken) =>
        db.FinancialCategories.AnyAsync(
            c => c.WorkspaceId == workspaceId && c.Name == name && c.Id != exceptCategoryId
                && (parentCategoryId == null ? c.ParentCategoryId == null && c.Type == type : c.ParentCategoryId == parentCategoryId),
            cancellationToken);

    public async Task<IReadOnlyList<FinancialCategoryDto>> ListAsync(
        Guid workspaceId, bool includeInactive, CancellationToken cancellationToken) =>
        await Project(db.FinancialCategories
                .Where(c => c.WorkspaceId == workspaceId && (includeInactive || c.IsActive))
                .OrderByDescending(c => c.IsActive)
                .ThenBy(c => c.Type == CategoryType.Income ? 0 : 1) // receitas antes de despesas
                .ThenBy(c => c.Name))
            .ToListAsync(cancellationToken);

    public Task<FinancialCategoryDto?> GetAsync(Guid workspaceId, Guid categoryId, CancellationToken cancellationToken) =>
        Project(db.FinancialCategories.Where(c => c.WorkspaceId == workspaceId && c.Id == categoryId))
            .SingleOrDefaultAsync(cancellationToken);

    private IQueryable<FinancialCategoryDto> Project(IQueryable<FinancialCategory> categories) =>
        from c in categories.AsNoTracking()
        join p in db.FinancialCategories.AsNoTracking() on c.ParentCategoryId equals (Guid?)p.Id into parents
        from parent in parents.DefaultIfEmpty()
        select new FinancialCategoryDto(c.Id, c.Name, c.Type, c.IsActive, c.CreatedAt, c.ParentCategoryId, parent == null ? null : parent.Name,
            c.IsActive && (parent == null || parent.IsActive));

    private static bool IsDuplicateName(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: FinancialCategoryConfiguration.UniqueNameIndex or FinancialCategoryConfiguration.UniqueSubcategoryNameIndex,
        };
}
