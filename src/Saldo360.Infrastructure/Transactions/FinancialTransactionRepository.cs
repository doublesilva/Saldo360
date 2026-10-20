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

    public Task<FinancialTransaction?> FindAsync(Guid workspaceId, Guid transactionId, CancellationToken cancellationToken) =>
        db.FinancialTransactions.SingleOrDefaultAsync(t => t.Id == transactionId && t.WorkspaceId == workspaceId, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);

    public Task<FinancialTransactionDto?> GetAsync(Guid workspaceId, Guid transactionId, CancellationToken cancellationToken) =>
        Project(db.FinancialTransactions.Where(t => t.WorkspaceId == workspaceId && t.Id == transactionId))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<TransactionPage> ListAsync(Guid workspaceId, TransactionFilter filter, CancellationToken cancellationToken)
    {
        var query = db.FinancialTransactions.Where(t => t.WorkspaceId == workspaceId);
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

    public Task<TransactionLink?> GetLinkAsync(Guid workspaceId, Guid transactionId, CancellationToken cancellationToken) =>
        db.FinancialTransactions
            .AsNoTracking()
            .Where(t => t.WorkspaceId == workspaceId && t.Id == transactionId)
            .Select(t => new TransactionLink(t.PlannedTransactionId))
            .SingleOrDefaultAsync(cancellationToken);

    public Task<AccountState?> GetAccountStateAsync(Guid workspaceId, Guid accountId, CancellationToken cancellationToken) =>
        db.FinancialAccounts
            .Where(a => a.WorkspaceId == workspaceId && a.Id == accountId)
            .Select(a => new AccountState(a.IsActive))
            .SingleOrDefaultAsync(cancellationToken);

    public Task<CategoryState?> GetCategoryStateAsync(Guid workspaceId, Guid categoryId, CancellationToken cancellationToken) =>
        db.FinancialCategories
            .Where(c => c.WorkspaceId == workspaceId && c.Id == categoryId)
            .Select(c => new CategoryState(c.Type, c.IsActive))
            .SingleOrDefaultAsync(cancellationToken);

    // Conta e categoria pela mesma família (as FKs garantem que existem). Ordena antes de projetar: data
    // decrescente e Id decrescente, estável entre páginas.
    private IQueryable<FinancialTransactionDto> Project(IQueryable<FinancialTransaction> transactions) =>
        from t in transactions.AsNoTracking()
        join a in db.FinancialAccounts on new { t.WorkspaceId, Id = t.AccountId } equals new { a.WorkspaceId, a.Id }
        join category in db.FinancialCategories on new { t.WorkspaceId, Id = t.CategoryId } equals new { category.WorkspaceId, Id = (Guid?)category.Id } into categories
        from c in categories.DefaultIfEmpty()
        orderby t.TransactionDate descending, t.Id descending
        select new FinancialTransactionDto(
            t.Id,
            t.Type,
            t.Amount,
            t.TransactionDate,
            t.Description,
            new TransactionReferenceDto(a.Id, a.Name, a.IsActive),
            c == null ? null : new TransactionReferenceDto(c.Id, c.Name, c.IsActive),
            t.IsCancelled,
            t.Currency,
            t.CreatedAt,
            t.UpdatedAt,
            t.CancelledAt,
            t.PlannedTransactionId,
            t.Origin);
}
