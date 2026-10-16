using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Families;
using Saldo360.Domain.Goals;
namespace Saldo360.Infrastructure.Persistence.Configurations;
internal sealed class FinancialGoalConfiguration : IEntityTypeConfiguration<FinancialGoal>
{
    public void Configure(EntityTypeBuilder<FinancialGoal> b)
    {
        b.ToTable("financial_goals", t => { t.HasCheckConstraint("ck_financial_goals_target_amount", "target_amount > 0"); t.HasCheckConstraint("ck_financial_goals_archived", "(status = 3) = (archived_at IS NOT NULL)"); t.HasCheckConstraint("ck_financial_goals_completed", "status <> 2 OR completed_at IS NOT NULL"); });
        b.HasKey(x=>x.Id); b.Property(x=>x.Id).ValueGeneratedNever(); b.Property(x=>x.Name).HasMaxLength(FinancialGoal.NameMaxLength).IsRequired(); b.Property(x=>x.Description).HasMaxLength(FinancialGoal.DescriptionMaxLength); b.Property(x=>x.TargetAmount).HasPrecision(18,2); b.Property(x=>x.GoalType).HasConversion<int>(); b.Property(x=>x.Priority).HasConversion<int>(); b.Property(x=>x.Status).HasConversion<int>(); b.Property(x=>x.TargetDate).HasColumnType("date"); b.HasAlternateKey(x=>new{x.FamilyId,x.Id}); b.HasIndex(x=>new{x.FamilyId,x.Status,x.TargetDate}); b.HasOne<Family>().WithMany().HasForeignKey(x=>x.FamilyId).OnDelete(DeleteBehavior.Restrict);
    }
}
