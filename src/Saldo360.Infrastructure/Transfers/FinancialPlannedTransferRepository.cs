using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Transfers;
using Saldo360.Domain.Transfers;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Transfers;

/// <summary>
/// Associação automática entre previsão e transferência realizada, SÓ quando é inequívoca: mesma origem, mesmo destino, mesmo valor e até <see cref="ToleranceDays"/> dias de distância, com
/// exatamente uma transferência candidata para a previsão E exatamente uma previsão candidata para a transferência. Qualquer ambiguidade fica para o usuário (candidatas na listagem).
/// Também libera a associação cuja transferência foi cancelada (a previsão volta a projetar). Idempotente; não cria nem apaga transferências.
/// </summary>
internal static class PlannedTransferReconciler
{
    public const int ToleranceDays = 3;

    public static async Task<int> ReconcileAsync(Saldo360DbContext db, Guid workspaceId, DateTimeOffset now, CancellationToken ct)
    {
        var changed = 0;
        var planned = await db.FinancialPlannedTransfers.Where(p => p.WorkspaceId == workspaceId && !p.IsCancelled).ToListAsync(ct);
        var transfers = await db.FinancialTransfers.AsNoTracking().Where(t => t.WorkspaceId == workspaceId)
            .Select(t => new { t.Id, t.SourceAccountId, t.DestinationAccountId, t.Amount, t.TransferDate, t.IsCancelled }).ToListAsync(ct);
        var byId = transfers.ToDictionary(t => t.Id);
        foreach (var item in planned.Where(p => p.RealizedTransferId is { } id && byId.TryGetValue(id, out var t) && t.IsCancelled))
            if (item.Release()) changed++;
        var linked = planned.Where(p => p.RealizedTransferId is not null).Select(p => p.RealizedTransferId!.Value).ToHashSet();
        var openPlans = planned.Where(p => p.RealizedTransferId is null).ToList();
        var freeTransfers = transfers.Where(t => !t.IsCancelled && !linked.Contains(t.Id)).ToList();
        bool Matches(FinancialPlannedTransfer p, dynamic t) =>
            p.SourceAccountId == t.SourceAccountId && p.DestinationAccountId == t.DestinationAccountId && p.Amount == t.Amount && Math.Abs(p.DueDate.DayNumber - t.TransferDate.DayNumber) <= ToleranceDays;
        foreach (var plan in openPlans)
        {
            var candidates = freeTransfers.Where(t => Matches(plan, t)).ToList();
            if (candidates.Count != 1) continue;
            var transfer = candidates[0];
            if (openPlans.Count(other => Matches(other, transfer)) != 1) continue;
            plan.Link(transfer.Id, PlannedTransferLinkOrigin.Inferred, now);
            freeTransfers.Remove(transfer);
            changed++;
        }
        if (changed > 0) await db.SaveChangesAsync(ct);
        return changed;
    }
}

internal sealed class FinancialPlannedTransferRepository(Saldo360DbContext db) : IFinancialPlannedTransferRepository
{
    public async Task AddAsync(FinancialPlannedTransfer planned, CancellationToken ct)
    {
        db.FinancialPlannedTransfers.Add(planned);
        await db.SaveChangesAsync(ct);
        // Uma transferência já realizada pode cumprir a previsão recém-criada.
        await PlannedTransferReconciler.ReconcileAsync(db, planned.WorkspaceId, DateTimeOffset.UtcNow, ct);
    }

    public Task<FinancialPlannedTransfer?> FindAsync(Guid workspaceId, Guid id, CancellationToken ct) =>
        db.FinancialPlannedTransfers.SingleOrDefaultAsync(p => p.WorkspaceId == workspaceId && p.Id == id, ct);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    public async Task<IReadOnlyList<PlannedTransferDto>> ListAsync(Guid workspaceId, DateOnly from, DateOnly to, bool includeCancelled, DateOnly today, CancellationToken ct)
    {
        var query = db.FinancialPlannedTransfers.AsNoTracking().Where(p => p.WorkspaceId == workspaceId && p.DueDate >= from && p.DueDate <= to);
        if (!includeCancelled) query = query.Where(p => !p.IsCancelled);
        return await Project(query, today, ct);
    }

    public async Task<PlannedTransferDto?> GetAsync(Guid workspaceId, Guid id, DateOnly today, CancellationToken ct) =>
        (await Project(db.FinancialPlannedTransfers.AsNoTracking().Where(p => p.WorkspaceId == workspaceId && p.Id == id), today, ct)).SingleOrDefault();

    public async Task<(Guid Source, Guid Destination, bool IsCancelled)?> FindTransferAsync(Guid workspaceId, Guid transferId, CancellationToken ct)
    {
        var row = await db.FinancialTransfers.AsNoTracking().Where(t => t.WorkspaceId == workspaceId && t.Id == transferId)
            .Select(t => new { t.SourceAccountId, t.DestinationAccountId, t.IsCancelled }).SingleOrDefaultAsync(ct);
        return row is null ? null : (row.SourceAccountId, row.DestinationAccountId, row.IsCancelled);
    }

    public Task<bool> IsTransferLinkedAsync(Guid workspaceId, Guid transferId, CancellationToken ct) =>
        db.FinancialPlannedTransfers.AnyAsync(p => p.WorkspaceId == workspaceId && p.RealizedTransferId == transferId, ct);

    private async Task<IReadOnlyList<PlannedTransferDto>> Project(IQueryable<FinancialPlannedTransfer> query, DateOnly today, CancellationToken ct)
    {
        var rows = await (from p in query.OrderBy(p => p.DueDate).ThenBy(p => p.Id)
                          join s in db.FinancialAccounts on new { p.WorkspaceId, Id = p.SourceAccountId } equals new { s.WorkspaceId, s.Id }
                          join d in db.FinancialAccounts on new { p.WorkspaceId, Id = p.DestinationAccountId } equals new { d.WorkspaceId, d.Id }
                          select new { p, Source = s, Destination = d }).ToListAsync(ct);
        var open = rows.Where(r => r.p.IsPending).ToList();
        var candidatesByPlan = new Dictionary<Guid, IReadOnlyList<PlannedTransferCandidateDto>>();
        if (open.Count > 0)
        {
            var workspaceId = open[0].p.WorkspaceId;
            var linked = db.FinancialPlannedTransfers.Where(p => p.WorkspaceId == workspaceId && p.RealizedTransferId != null).Select(p => p.RealizedTransferId!.Value);
            var free = await db.FinancialTransfers.AsNoTracking().Where(t => t.WorkspaceId == workspaceId && !t.IsCancelled && !linked.Contains(t.Id))
                .Select(t => new { t.Id, t.SourceAccountId, t.DestinationAccountId, t.Amount, t.TransferDate, t.Description }).ToListAsync(ct);
            foreach (var r in open)
                candidatesByPlan[r.p.Id] = free.Where(t => t.SourceAccountId == r.p.SourceAccountId && t.DestinationAccountId == r.p.DestinationAccountId && Math.Abs(t.TransferDate.DayNumber - r.p.DueDate.DayNumber) <= 7)
                    .OrderBy(t => Math.Abs(t.TransferDate.DayNumber - r.p.DueDate.DayNumber)).Select(t => new PlannedTransferCandidateDto(t.Id, t.TransferDate, t.Amount, t.Description)).ToList();
        }
        return rows.Select(r => new PlannedTransferDto(r.p.Id, new(r.Source.Id, r.Source.Name, r.Source.Scope, r.Source.IsActive), new(r.Destination.Id, r.Destination.Name, r.Destination.Scope, r.Destination.IsActive),
            r.p.Amount, r.p.DueDate, r.p.Description, PlannedTransferStatus.Of(r.p.IsCancelled, r.p.RealizedTransferId, r.p.DueDate, today), r.p.RealizedTransferId, r.p.LinkOrigin?.ToString(),
            r.p.CreatedAt, r.p.CancelledAt, candidatesByPlan.GetValueOrDefault(r.p.Id) ?? [])).ToList();
    }
}
