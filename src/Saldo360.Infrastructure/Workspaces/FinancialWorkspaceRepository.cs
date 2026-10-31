using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Categories;
using Saldo360.Application.Workspaces;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Workspaces;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Workspaces;

internal sealed class FinancialWorkspaceRepository(Saldo360DbContext db, IDefaultCategoryCatalog defaultCategories) : IFinancialWorkspaceRepository
{
    /// <summary>Sem catálogo padrão: o orçamento nasce sem categorias (usado por testes que montam o próprio cadastro).</summary>
    public FinancialWorkspaceRepository(Saldo360DbContext db) : this(db, EmptyDefaultCategoryCatalog.Instance)
    {
    }

    internal async Task AddAsync(FinancialWorkspace workspace, CancellationToken ct)
    {
        db.FinancialWorkspaces.Add(workspace);
        await db.SaveChangesAsync(ct);
    }
    public async Task<FinancialWorkspaceRole?> GetOwnerRoleAsync(Guid workspaceId, Guid userId, CancellationToken ct) =>
        await db.FinancialWorkspaces.AsNoTracking().AnyAsync(x => x.Id == workspaceId && x.OwnerUserId == userId
            && x.Type == FinancialWorkspaceType.Personal && x.IsActive, ct) ? FinancialWorkspaceRole.Owner : null;

    public async Task<FinancialWorkspace> EnsurePersonalAsync(Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Lock the existing identity, not a missing workspace. All concurrent ensures serialize here.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM application_users WHERE id = {userId} FOR UPDATE", ct);
        var existing = await db.FinancialWorkspaces.SingleOrDefaultAsync(x => x.OwnerUserId == userId && x.Type == FinancialWorkspaceType.Personal, ct);
        if (existing is not null) { await transaction.CommitAsync(ct); return existing; }
        var workspace = FinancialWorkspace.CreatePersonal(userId, now);
        // O catálogo entra na mesma transação da criação e só aqui: um orçamento existente nunca é reinicializado, nem quando fica sem categorias.
        var categories = DefaultCategoryPlanner.Plan(defaultCategories, [])
            .CreateEntities(workspace.Id, userId, now, new Dictionary<Guid, FinancialCategory>());
        workspace.MarkDefaultCategoriesInitialized(defaultCategories.Version, now);
        db.FinancialWorkspaces.Add(workspace);
        db.FinancialCategories.AddRange(categories);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return workspace;
    }
}
