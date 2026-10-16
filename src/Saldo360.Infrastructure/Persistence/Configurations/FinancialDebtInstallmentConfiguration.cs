using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Debts;
using Saldo360.Domain.Families;

namespace Saldo360.Infrastructure.Persistence.Configurations;
internal sealed class FinancialDebtInstallmentConfiguration : IEntityTypeConfiguration<FinancialDebtInstallment>
{
 public void Configure(EntityTypeBuilder<FinancialDebtInstallment> b){b.ToTable("financial_debt_installments",t=>{t.HasCheckConstraint("ck_debt_installment_number_positive","installment_number > 0");t.HasCheckConstraint("ck_debt_installment_amount_positive","scheduled_amount > 0");});b.HasKey(x=>x.Id);b.Property(x=>x.Id).ValueGeneratedNever();b.Property(x=>x.ScheduledAmount).HasColumnType("numeric");b.Property(x=>x.DueDate).HasColumnType("date");foreach(var p in new[]{"PrincipalComponent","InterestComponent","InsuranceComponent","OtherChargesComponent"}) b.Property(p).HasColumnType("numeric");b.HasIndex(x=>new{x.FamilyId,x.DebtId,x.InstallmentNumber}).IsUnique();b.HasIndex(x=>new{x.FamilyId,x.DueDate});b.HasOne<Family>().WithMany().HasForeignKey(x=>x.FamilyId).OnDelete(DeleteBehavior.Restrict);b.HasOne<FinancialDebt>().WithMany().HasForeignKey(x=>new{x.FamilyId,x.DebtId}).HasPrincipalKey(x=>new{x.FamilyId,x.Id}).OnDelete(DeleteBehavior.Restrict);}
}
