using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Goals;
using Saldo360.Domain.Goals;
using Saldo360.Infrastructure.Persistence;
namespace Saldo360.Infrastructure.Goals;
internal sealed class FinancialGoalRepository(Saldo360DbContext db) : IFinancialGoalRepository
{
    public async Task AddAsync(FinancialGoal goal,CancellationToken ct){db.FinancialGoals.Add(goal);await db.SaveChangesAsync(ct);}
    public Task<FinancialGoal?> FindAsync(Guid familyId,Guid id,CancellationToken ct)=>db.FinancialGoals.SingleOrDefaultAsync(x=>x.FamilyId==familyId&&x.Id==id,ct);
    public Task SaveAsync(CancellationToken ct)=>db.SaveChangesAsync(ct);
    public Task<FinancialGoalDto?> GetAsync(Guid familyId,Guid id,CancellationToken ct)=>Project(db.FinancialGoals.Where(x=>x.FamilyId==familyId&&x.Id==id)).SingleOrDefaultAsync(ct);
    public async Task<IReadOnlyList<FinancialGoalDto>> ListAsync(Guid familyId,bool includeArchived,CancellationToken ct){var q=db.FinancialGoals.Where(x=>x.FamilyId==familyId);if(!includeArchived)q=q.Where(x=>x.Status!=FinancialGoalStatus.Archived);return await Project(q.OrderBy(x=>x.Status).ThenBy(x=>x.TargetDate).ThenBy(x=>x.Name)).ToListAsync(ct);}
    private static IQueryable<FinancialGoalDto> Project(IQueryable<FinancialGoal> q)=>q.Select(x=>new FinancialGoalDto(x.Id,x.FamilyId,x.Name,x.Description,x.GoalType,x.TargetAmount,x.TargetDate,x.Priority,x.Status,x.Currency,x.CreatedAt,x.UpdatedAt,x.CompletedAt,x.ArchivedAt));
}
