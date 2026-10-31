using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Common;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class OverdraftRecoverySnapshotConfiguration : IEntityTypeConfiguration<OverdraftRecoverySnapshot>
{
    public void Configure(EntityTypeBuilder<OverdraftRecoverySnapshot> builder)
    {
        builder.ToTable("overdraft_recovery_snapshots", table =>
        {
            table.HasCheckConstraint("ck_overdraft_recovery_snapshot_competence", "extract(day from competence) = 1");
            table.HasCheckConstraint("ck_overdraft_recovery_snapshot_money", $"abs(opening_balance) < {Money.Limit} AND abs(planned_income) < {Money.Limit} AND abs(planned_expense) < {Money.Limit} AND abs(planned_closing_balance) < {Money.Limit} AND abs(recovery_target) < {Money.Limit}");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Competence).HasColumnType("date");
        builder.Property(x => x.ReferenceDate).HasColumnType("date");
        builder.Property(x => x.OpeningBalance).HasColumnType("numeric");
        builder.Property(x => x.PlannedIncome).HasColumnType("numeric");
        builder.Property(x => x.PlannedExpense).HasColumnType("numeric");
        builder.Property(x => x.PlannedClosingBalance).HasColumnType("numeric");
        builder.Property(x => x.RecoveryTarget).HasColumnType("numeric");
        builder.Property(x => x.Quality).HasMaxLength(20);
        builder.Property(x => x.Composition).HasMaxLength(1000);
        builder.HasIndex(x => new { x.WorkspaceId, x.AccountId, x.Competence }).IsUnique();
        builder.HasOne<FinancialAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
    }
}
