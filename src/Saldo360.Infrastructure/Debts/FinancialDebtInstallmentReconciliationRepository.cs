using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Saldo360.Application.Debts;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Debts;
using Saldo360.Infrastructure.Persistence;
#pragma warning disable CS8602
namespace Saldo360.Infrastructure.Debts;
internal sealed class FinancialDebtInstallmentReconciliationRepository(Saldo360DbContext db) : IFinancialDebtInstallmentReconciliationRepository
{
 public async Task<IReadOnlyList<DebtInstallmentReconciliationDto>> ListAsync(Guid f,CancellationToken ct)=>await db.FinancialDebtInstallmentReconciliations.AsNoTracking().Where(x=>x.FamilyId==f).OrderByDescending(x=>x.CreatedAt).Select(x=>new DebtInstallmentReconciliationDto(x.Id,x.FamilyId,x.DebtInstallmentId,x.PlannedTransactionId,x.CreatedAt,x.RemovedAt)).ToListAsync(ct);
 public async Task<(bool InstallmentExists,bool PlannedExists,bool PlannedEligible,bool InstallmentEligible,bool AccountCompatible,bool AlreadyLinked)> EligibilityAsync(Guid f,Guid iid,Guid pid,CancellationToken ct){var i=await(from x in db.FinancialDebtInstallments where x.FamilyId==f&&x.Id==iid join d in db.FinancialDebts on new{x.FamilyId,x.DebtId} equals new{d.FamilyId,DebtId=d.Id} select new{Installment=x,d.ResponsibleAccountId}).SingleOrDefaultAsync(ct);var p=await db.FinancialPlannedTransactions.Where(x=>x.FamilyId==f&&x.Id==pid).Select(x=>new{x.AccountId,x.Type,x.IsCancelled}).SingleOrDefaultAsync(ct);if(i is null||p is null)return(i is not null,p is not null,false,false,false,false);var paid=await db.FinancialDebtPayments.Where(x=>x.FamilyId==f&&x.InstallmentId==iid&&x.CancelledAt==null).SumAsync(x=>(decimal?)x.Amount,ct)??0m;var linked=await db.FinancialDebtInstallmentReconciliations.AnyAsync(x=>x.FamilyId==f&&x.RemovedAt==null&&(x.DebtInstallmentId==iid||x.PlannedTransactionId==pid),ct);var realized=await db.FinancialTransactions.AnyAsync(x=>x.FamilyId==f&&x.PlannedTransactionId==pid&&!x.IsCancelled,ct);return(i is not null,p is not null,p.Type==CategoryType.Expense&&!p.IsCancelled&&!realized,i.Installment.ScheduledAmount>paid,i.ResponsibleAccountId is null||i.ResponsibleAccountId==p.AccountId,linked);}
 public async Task<DebtInstallmentReconciliationDto?> CreateAsync(Guid f,Guid iid,Guid pid,Guid u,DateTimeOffset now,CancellationToken ct){await using var tx=await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,ct);var e=FinancialDebtInstallmentReconciliation.Create(f,iid,pid,u,now);db.FinancialDebtInstallmentReconciliations.Add(e);try{await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return await db.FinancialDebtInstallmentReconciliations.Where(x=>x.Id==e.Id).Select(x=>new DebtInstallmentReconciliationDto(x.Id,x.FamilyId,x.DebtInstallmentId,x.PlannedTransactionId,x.CreatedAt,x.RemovedAt)).SingleAsync(ct);}catch(DbUpdateException ex)when(ex.InnerException is PostgresException{SqlState:PostgresErrorCodes.UniqueViolation}){return null;}}
 public async Task<bool> RemoveAsync(Guid f,Guid id,Guid u,DateTimeOffset now,CancellationToken ct){var e=await db.FinancialDebtInstallmentReconciliations.SingleOrDefaultAsync(x=>x.FamilyId==f&&x.Id==id&&x.RemovedAt==null,ct);if(e is null)return false;e.Remove(u,now);await db.SaveChangesAsync(ct);return true;}
}
﻿#nullable disable
