using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Transactions;
using Saldo360.Domain.Transactions;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Transactions;

internal sealed class FinancialTransactionRepository(Saldo360DbContext db) : IFinancialTransactionRepository
{
    public async Task AddAsync(FinancialTransaction transaction, CancellationToken cancellationToken)
    {
        db.FinancialTransactions.Add(transaction);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<FinancialTransaction?> FindAsync(Guid familyId, Guid transactionId, CancellationToken cancellationToken) =>
        db.FinancialTransactions.SingleOrDefaultAsync(t => t.Id == transactionId && t.FamilyId == familyId, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);

    public Task<FinancialTransactionDto?> GetAsync(Guid familyId, Guid transactionId, CancellationToken cancellationToken) =>
        Project(db.FinancialTransactions.Where(t => t.FamilyId == familyId && t.Id == transactionId))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<TransactionPage> ListAsync(Guid familyId, TransactionFilter filter, CancellationToken cancellationToken)
    {
        var query = db.FinancialTransactions.Where(t => t.FamilyId == familyId);
        if (!filter.IncludeCancelled)
        {
            query = query.Where(t => !t.IsCancelled);
        }

        if (filter.From is { } from)
        {
            query = query.Where(t => t.TransactionDate >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(t => t.TransactionDate <= to);
        }

        if (filter.AccountId is { } accountId)
        {
            query = query.Where(t => t.AccountId == accountId);
        }

        if (filter.CategoryId is { } categoryId)
        {
            query = query.Where(t => t.CategoryId == categoryId);
        }

        if (filter.Type is { } type)
        {
            query = query.Where(t => t.Type == type);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await Project(query)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(cancellationToken);
        return new TransactionPage(items, filter.Page, filter.PageSize, total);
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

    // Conta e categoria pela mesma família (as FKs garantem que existem). Ordena antes de projetar: data
    // decrescente e Id decrescente, estável entre páginas.
    private IQueryable<FinancialTransactionDto> Project(IQueryable<FinancialTransaction> transactions) =>
        from t in transactions.AsNoTracking()
        join a in db.FinancialAccounts on new { t.FamilyId, Id = t.AccountId } equals new { a.FamilyId, a.Id }
        join c in db.FinancialCategories on new { t.FamilyId, Id = t.CategoryId } equals new { c.FamilyId, c.Id }
        orderby t.TransactionDate descending, t.Id descending
        select new FinancialTransactionDto(
            t.Id,
            t.Type,
            t.Amount,
            t.TransactionDate,
            t.Description,
            new TransactionReferenceDto(a.Id, a.Name, a.IsActive),
            new TransactionReferenceDto(c.Id, c.Name, c.IsActive),
            t.IsCancelled,
            t.Currency,
            t.CreatedAt,
            t.UpdatedAt,
            t.CancelledAt);
}
