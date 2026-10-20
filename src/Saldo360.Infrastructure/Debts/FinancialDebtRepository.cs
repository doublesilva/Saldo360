using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Debts;
using Saldo360.Domain.Debts;
using Saldo360.Infrastructure.Persistence;
namespace Saldo360.Infrastructure.Debts;
internal sealed class FinancialDebtRepository(Saldo360DbContext db):IFinancialDebtRepository
{
 public async Task AddAsync(FinancialDebt debt,CancellationToken ct){db.FinancialDebts.Add(debt);await db.SaveChangesAsync(ct);} public Task<FinancialDebt?> FindAsync(Guid workspaceId,Guid id,CancellationToken ct)=>db.FinancialDebts.SingleOrDefaultAsync(x=>x.WorkspaceId==workspaceId&&x.Id==id,ct); public Task SaveAsync(CancellationToken ct)=>db.SaveChangesAsync(ct);
 public Task<DebtDto?> GetAsync(Guid workspaceId,Guid id,CancellationToken ct)=>Project(db.FinancialDebts.Where(x=>x.WorkspaceId==workspaceId&&x.Id==id)).SingleOrDefaultAsync(ct);
 public async Task<IReadOnlyList<DebtDto>> ListAsync(Guid workspaceId,bool includeArchived,CancellationToken ct){var q=db.FinancialDebts.Where(x=>x.WorkspaceId==workspaceId);if(!includeArchived)q=q.Where(x=>x.Status!=DebtStatus.Archived);return await Project(q.OrderBy(x=>x.Name)).ToListAsync(ct);}
 private static IQueryable<DebtDto> Project(IQueryable<FinancialDebt> q)=>q.Select(x=>new DebtDto(x.Id,x.WorkspaceId,x.Name,x.Description,x.DebtType,x.LenderName,x.ResponsibleAccountId,x.OriginalPrincipal,x.OutstandingPrincipal,x.OutstandingBalance,x.BalanceReferenceDate,x.ContractDate,x.FirstDueDate,x.InstallmentCount,x.PaidInstallmentCount,x.InstallmentAmount,x.InterestRate,x.InterestRatePeriod,x.AmortizationMethod,x.Status,x.Currency,x.CreatedAt,x.UpdatedAt,x.ArchivedAt));
}
