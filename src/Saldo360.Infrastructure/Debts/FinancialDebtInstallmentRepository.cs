using Microsoft.EntityFrameworkCore;
using Saldo360.Application.Debts;
using Saldo360.Domain.Debts;
using Saldo360.Domain.Transactions;
using Saldo360.Infrastructure.Persistence;

namespace Saldo360.Infrastructure.Debts;
internal sealed class FinancialDebtInstallmentRepository(Saldo360DbContext db) : IFinancialDebtInstallmentRepository
{
 public Task<FinancialDebt?> GetDebtAsync(Guid f,Guid d,CancellationToken ct)=>db.FinancialDebts.SingleOrDefaultAsync(x=>x.FamilyId==f&&x.Id==d,ct);
 public Task<FinancialDebtInstallment?> GetInstallmentAsync(Guid f,Guid d,Guid i,CancellationToken ct)=>db.FinancialDebtInstallments.SingleOrDefaultAsync(x=>x.FamilyId==f&&x.DebtId==d&&x.Id==i,ct);
 public async Task<IReadOnlyList<InstallmentDto>> ListInstallmentsAsync(Guid f,Guid d,CancellationToken ct){var rows=await db.FinancialDebtInstallments.Where(x=>x.FamilyId==f&&x.DebtId==d).OrderBy(x=>x.InstallmentNumber).Select(x=>new{x,paid=db.FinancialDebtPayments.Where(p=>p.FamilyId==f&&p.InstallmentId==x.Id&&p.CancelledAt==null).Sum(p=>(decimal?)p.Amount)??0m}).ToListAsync(ct);var today=DateOnly.FromDateTime(DateTime.UtcNow.Date);return rows.Select(r=>new InstallmentDto(r.x.Id,f,d,r.x.InstallmentNumber,r.x.DueDate,r.x.ScheduledAmount,r.x.PrincipalComponent,r.x.InterestComponent,r.x.InsuranceComponent,r.x.OtherChargesComponent,r.paid,Math.Max(0,r.x.ScheduledAmount-r.paid),r.x.CancelledAt is not null?"Cancelled":r.paid>=r.x.ScheduledAmount?"Paid":r.paid>0?"PartiallyPaid":r.x.DueDate<today?"Overdue":"Pending",r.x.CreatedAt)).ToList();}
 public async Task<IReadOnlyList<PaymentDto>> ListPaymentsAsync(Guid f,Guid i,CancellationToken ct)=>await db.FinancialDebtPayments.Where(x=>x.FamilyId==f&&x.InstallmentId==i).OrderBy(x=>x.CreatedAt).Select(x=>new PaymentDto(x.Id,x.InstallmentId,x.FinancialAccountId,x.PaymentDate,x.Amount,x.PrincipalPaid,x.InterestPaid,x.BankTransactionId,x.OperationId,x.CancelledAt!=null,x.CreatedAt)).ToListAsync(ct);
 public Task<FinancialDebtPayment?> FindPaymentByOperationAsync(Guid f,string op,CancellationToken ct)=>db.FinancialDebtPayments.SingleOrDefaultAsync(x=>x.FamilyId==f&&x.OperationId==op,ct);
 public Task<bool> PaymentPayloadMatchesAsync(Guid f,string op,Guid d,Guid i,Guid a,DateOnly date,decimal amount,decimal? principal,decimal? interest,decimal? insurance,decimal? other,CancellationToken ct)=>db.FinancialDebtPayments.AsNoTracking().AnyAsync(x=>x.FamilyId==f&&x.OperationId==op&&x.DebtId==d&&x.InstallmentId==i&&x.FinancialAccountId==a&&x.PaymentDate==date&&x.Amount==amount&&x.PrincipalPaid==principal&&x.InterestPaid==interest&&x.InsurancePaid==insurance&&x.OtherChargesPaid==other,ct);
 public Task<PaymentDto?> GetPaymentAsync(Guid f,Guid id,CancellationToken ct)=>db.FinancialDebtPayments.Where(x=>x.FamilyId==f&&x.Id==id).Select(x=>new PaymentDto(x.Id,x.InstallmentId,x.FinancialAccountId,x.PaymentDate,x.Amount,x.PrincipalPaid,x.InterestPaid,x.BankTransactionId,x.OperationId,x.CancelledAt!=null,x.CreatedAt)).SingleOrDefaultAsync(ct);
 public async Task AddInstallmentAsync(FinancialDebtInstallment item,CancellationToken ct){db.FinancialDebtInstallments.Add(item);await db.SaveChangesAsync(ct);}
 public async Task<(FinancialDebtPayment Payment, FinancialTransaction Transaction)?> AddPaymentAsync(FinancialDebtPayment p,FinancialTransaction t,CancellationToken ct){await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,ct);db.FinancialTransactions.Add(t);db.FinancialDebtPayments.Add(p);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return(p,t);}
 public Task SaveAsync(CancellationToken ct)=>db.SaveChangesAsync(ct);
 public async Task<bool> CancelPaymentAsync(Guid f,Guid id,Guid userId,DateTimeOffset now,CancellationToken ct){await using var tx=await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable,ct);var p=await db.FinancialDebtPayments.SingleOrDefaultAsync(x=>x.FamilyId==f&&x.Id==id,ct);if(p is null)return false;if(!p.Cancel(now))return true;var t=await db.FinancialTransactions.SingleAsync(x=>x.FamilyId==f&&x.Id==p.BankTransactionId,ct);t.Cancel(userId,now);await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return true;}
}
