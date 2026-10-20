using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Recurrences;
using Saldo360.Domain.Recurrences;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Recurrences;

internal sealed class FinancialRecurrenceRepository(Saldo360DbContext db) : IFinancialRecurrenceRepository
{
    public async Task AddAsync(FinancialRecurrence recurrence,CancellationToken ct){db.FinancialRecurrences.Add(recurrence);await db.SaveChangesAsync(ct);}
    public Task<FinancialRecurrence?> FindEntityAsync(Guid workspaceId,Guid id,CancellationToken ct)=>db.FinancialRecurrences.SingleOrDefaultAsync(x=>x.WorkspaceId==workspaceId&&x.Id==id,ct);
    public Task SaveAsync(CancellationToken ct)=>db.SaveChangesAsync(ct);
    public Task<bool> HasMaterializationsAsync(Guid workspaceId, Guid recurrenceId, CancellationToken ct) => db.FinancialRecurrenceMaterializations.AnyAsync(x => x.WorkspaceId == workspaceId && x.RecurrenceId == recurrenceId, ct);
    public Task<RecurrenceDto?> GetAsync(Guid workspaceId,Guid id,CancellationToken ct)=>Project(db.FinancialRecurrences.Where(x=>x.WorkspaceId==workspaceId&&x.Id==id)).SingleOrDefaultAsync(ct);
    public async Task<IReadOnlyList<RecurrenceDto>> ListAsync(Guid workspaceId, bool includeArchived, CancellationToken ct)
    {
        var query = db.FinancialRecurrences.Where(x => x.WorkspaceId == workspaceId);
        if (!includeArchived)
        {
            query = query.Where(x => x.Status != RecurrenceStatus.Archived);
        }

        // Order mapped columns before constructing the positional DTO so EF can translate the query.
        return await Project(query.OrderBy(x => x.StartDate).ThenBy(x => x.Id)).ToListAsync(ct);
    }
    private IQueryable<RecurrenceDto> Project(IQueryable<FinancialRecurrence> q)=>q.Select(x=>new RecurrenceDto(x.Id,x.WorkspaceId,x.FinancialAccountId,x.FinancialCategoryId,x.Type,x.Description,x.Amount,x.IsEstimated,x.Frequency,x.Interval,x.StartDate,x.EndDate,x.OccurrenceCount,x.DayOfMonth,x.Status,x.Currency,x.CreatedAt,x.UpdatedAt,x.ArchivedAt));
}
