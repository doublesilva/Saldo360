using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Planning;
using Saldo360.Application.Transactions;
using Saldo360.Domain.Planning;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Planning;

internal sealed class FinancialPlannedTransactionRepository(Saldo360DbContext db) : IFinancialPlannedTransactionRepository
{
    public async Task AddAsync(FinancialPlannedTransaction planned, CancellationToken cancellationToken)
    {
        db.FinancialPlannedTransactions.Add(planned);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<FinancialPlannedTransaction?> FindAsync(Guid familyId, Guid plannedId, CancellationToken cancellationToken) =>
        db.FinancialPlannedTransactions.SingleOrDefaultAsync(p => p.Id == plannedId && p.FamilyId == familyId, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);

    public Task<FinancialPlannedTransactionDto?> GetAsync(Guid familyId, Guid plannedId, CancellationToken cancellationToken) =>
        Project(db.FinancialPlannedTransactions.Where(p => p.FamilyId == familyId && p.Id == plannedId)).SingleOrDefaultAsync(cancellationToken);

    public async Task<PlannedTransactionPage> ListAsync(Guid familyId, PlannedTransactionFilter filter, CancellationToken cancellationToken)
    {
        var query = db.FinancialPlannedTransactions.Where(p => p.FamilyId == familyId);
        if (!filter.IncludeCancelled)
        {
            query = query.Where(p => !p.IsCancelled);
        }

        if (filter.From is { } from)
        {
            query = query.Where(p => p.DueDate >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(p => p.DueDate <= to);
        }

        if (filter.AccountId is { } accountId)
        {
            query = query.Where(p => p.AccountId == accountId);
        }

        if (filter.CategoryId is { } categoryId)
        {
            query = query.Where(p => p.CategoryId == categoryId);
        }

        if (filter.Type is { } type)
        {
            query = query.Where(p => p.Type == type);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await Project(query)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(cancellationToken);
        return new PlannedTransactionPage(items, filter.Page, filter.PageSize, total);
    }

    public Task<AccountState?> GetAccountStateAsync(Guid familyId, Guid accountId, CancellationToken cancellationToken) =>
        db.FinancialAccounts
            .Where(a => a.FamilyId == familyId && a.Id == accountId)
            .Select(a => new AccountState(a.IsActive))
            .SingleOrDefaultAsync(cancellationToken);

    public Task<CategoryState?> GetCategoryStateAsync(Guid familyId, Guid categoryId, CancellationToken cancellationToken) =>
        db.FinancialCategories
            .Where(c => c.FamilyId == familyId && c.Id == categoryId)
            .Select(c => new CategoryState(c.Type, c.IsActive))
            .SingleOrDefaultAsync(cancellationToken);

    // Conta e categoria pela mesma família (as FKs garantem que existem). Ordena antes de projetar: data prevista
    // crescente (as mais próximas primeiro) e Id crescente, estável entre páginas.
    private IQueryable<FinancialPlannedTransactionDto> Project(IQueryable<FinancialPlannedTransaction> planned) =>
        from p in planned.AsNoTracking()
        join a in db.FinancialAccounts on new { p.FamilyId, Id = p.AccountId } equals new { a.FamilyId, a.Id }
        join c in db.FinancialCategories on new { p.FamilyId, Id = p.CategoryId } equals new { c.FamilyId, c.Id }
        orderby p.DueDate, p.Id
        select new FinancialPlannedTransactionDto(
            p.Id,
            p.Type,
            p.Amount,
            p.DueDate,
            p.Description,
            new TransactionReferenceDto(a.Id, a.Name, a.IsActive),
            new TransactionReferenceDto(c.Id, c.Name, c.IsActive),
            p.IsCancelled,
            p.Currency,
            p.CreatedAt,
            p.UpdatedAt,
            p.CancelledAt);
}
