using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Debts;
using Saldo360.Domain.Workspaces;

namespace Saldo360.Infrastructure.Persistence.Configurations;
internal sealed class FinancialDebtInstallmentConfiguration : IEntityTypeConfiguration<FinancialDebtInstallment>
{
 public void Configure(EntityTypeBuilder<FinancialDebtInstallment> b){b.ToTable("financial_debt_installments",t=>{t.HasCheckConstraint("ck_debt_installment_number_positive","installment_number > 0");t.HasCheckConstraint("ck_debt_installment_amount_positive","scheduled_amount > 0");});b.Property(x=>x.Nature).HasColumnName("expense_nature").HasConversion<string>().HasMaxLength(20).HasDefaultValue(Saldo360.Domain.Categories.ExpenseNature.Undefined);b.ToTable(t=>t.HasCheckConstraint("ck_debt_installment_expense_nature",ExpenseNatureCheck.Sql(null,"expense_nature")));b.HasKey(x=>x.Id);b.Property(x=>x.Id).ValueGeneratedNever();b.Property(x=>x.ScheduledAmount).HasColumnType("numeric");b.Property(x=>x.DueDate).HasColumnType("date");foreach(var p in new[]{"PrincipalComponent","InterestComponent","InsuranceComponent","OtherChargesComponent"}) b.Property(p).HasColumnType("numeric");b.HasIndex(x=>new{x.WorkspaceId,x.DebtId,x.InstallmentNumber}).IsUnique();b.HasIndex(x=>new{x.WorkspaceId,x.DueDate});b.HasOne<FinancialWorkspace>().WithMany().HasForeignKey(x=>x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);b.HasOne<FinancialDebt>().WithMany().HasForeignKey(x=>new{x.WorkspaceId,x.DebtId}).HasPrincipalKey(x=>new{x.WorkspaceId,x.Id}).OnDelete(DeleteBehavior.Restrict);b.HasOne<Saldo360.Domain.Categories.FinancialCategory>().WithMany().HasForeignKey(x=>new{x.WorkspaceId,x.CategoryId}).HasPrincipalKey(c=>new{c.WorkspaceId,c.Id}).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_debt_installments_categories_same_workspace");}
}
