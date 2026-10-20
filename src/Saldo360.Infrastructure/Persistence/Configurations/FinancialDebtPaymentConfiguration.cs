using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Debts;
using Saldo360.Domain.Workspaces;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Transactions;

namespace Saldo360.Infrastructure.Persistence.Configurations;
internal sealed class FinancialDebtPaymentConfiguration : IEntityTypeConfiguration<FinancialDebtPayment>
{
 public void Configure(EntityTypeBuilder<FinancialDebtPayment> b){b.ToTable("financial_debt_payments",t=>{t.HasCheckConstraint("ck_debt_payment_amount_positive","amount > 0");t.HasCheckConstraint("ck_debt_payment_cancelled_pair","(cancelled_at IS NULL) OR cancelled_at IS NOT NULL");});b.HasKey(x=>x.Id);b.Property(x=>x.Id).ValueGeneratedNever();b.Property(x=>x.Amount).HasColumnType("numeric");b.Property(x=>x.PaymentDate).HasColumnType("date");b.Property(x=>x.OperationId).HasMaxLength(100).IsRequired();b.HasIndex(x=>new{x.WorkspaceId,x.OperationId}).IsUnique();b.HasIndex(x=>new{x.WorkspaceId,x.InstallmentId});b.HasOne<FinancialWorkspace>().WithMany().HasForeignKey(x=>x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);b.HasOne<FinancialDebtInstallment>().WithMany().HasForeignKey(x=>new{x.WorkspaceId,x.InstallmentId}).HasPrincipalKey(x=>new{x.WorkspaceId,x.Id}).OnDelete(DeleteBehavior.Restrict);b.HasOne<FinancialAccount>().WithMany().HasForeignKey(x=>new{x.WorkspaceId,x.FinancialAccountId}).HasPrincipalKey(x=>new{x.WorkspaceId,x.Id}).OnDelete(DeleteBehavior.Restrict);b.HasOne<FinancialTransaction>().WithMany().HasForeignKey(x=>new{x.WorkspaceId,BankTransactionId=x.BankTransactionId}).HasPrincipalKey(x=>new{x.WorkspaceId,x.Id}).OnDelete(DeleteBehavior.Restrict);}
}
