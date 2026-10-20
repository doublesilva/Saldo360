using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Transfers;
using Saldo360.Domain.Transfers;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Transfers;

internal sealed class FinancialTransferRepository(Saldo360DbContext db) : IFinancialTransferRepository
{
    public async Task AddAsync(FinancialTransfer transfer, CancellationToken cancellationToken)
    {
        db.FinancialTransfers.Add(transfer);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<FinancialTransfer?> FindAsync(Guid workspaceId, Guid transferId, CancellationToken cancellationToken) =>
        db.FinancialTransfers.SingleOrDefaultAsync(t => t.Id == transferId && t.WorkspaceId == workspaceId, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);

    public Task<FinancialTransferDto?> GetAsync(Guid workspaceId, Guid transferId, CancellationToken cancellationToken) =>
        Project(db.FinancialTransfers.Where(t => t.WorkspaceId == workspaceId && t.Id == transferId)).SingleOrDefaultAsync(cancellationToken);

    public async Task<TransferPage> ListAsync(Guid workspaceId, TransferFilter filter, CancellationToken cancellationToken)
    {
        var query = db.FinancialTransfers.Where(t => t.WorkspaceId == workspaceId);
        if (!filter.IncludeCancelled)
        {
            query = query.Where(t => !t.IsCancelled);
        }

        if (filter.From is { } from)
        {
            query = query.Where(t => t.TransferDate >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(t => t.TransferDate <= to);
        }

        if (filter.AccountId is { } accountId)
        {
            query = query.Where(t => t.SourceAccountId == accountId || t.DestinationAccountId == accountId);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await Project(query)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync(cancellationToken);
        return new TransferPage(items, filter.Page, filter.PageSize, total);
    }

    public Task<bool?> IsAccountActiveAsync(Guid workspaceId, Guid accountId, CancellationToken cancellationToken) =>
        db.FinancialAccounts
            .Where(a => a.WorkspaceId == workspaceId && a.Id == accountId)
            .Select(a => (bool?)a.IsActive)
            .SingleOrDefaultAsync(cancellationToken);

    // Origem e destino pela mesma família (as FKs garantem que existem). Ordena antes de projetar: data
    // decrescente e Id decrescente, estável entre páginas.
    private IQueryable<FinancialTransferDto> Project(IQueryable<FinancialTransfer> transfers) =>
        from t in transfers.AsNoTracking()
        join source in db.FinancialAccounts on new { t.WorkspaceId, Id = t.SourceAccountId } equals new { source.WorkspaceId, source.Id }
        join destination in db.FinancialAccounts on new { t.WorkspaceId, Id = t.DestinationAccountId } equals new { destination.WorkspaceId, destination.Id }
        orderby t.TransferDate descending, t.Id descending
        select new FinancialTransferDto(
            t.Id,
            t.Amount,
            t.TransferDate,
            t.Description,
            new TransferAccountDto(source.Id, source.Name, source.Scope, source.IsActive),
            new TransferAccountDto(destination.Id, destination.Name, destination.Scope, destination.IsActive),
            t.IsCancelled,
            t.Currency,
            t.CreatedAt,
            t.CancelledAt);
}
