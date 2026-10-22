using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Bootstrap;
using Saldo360.Domain.Budgets;
using Saldo360.Domain.Workspaces;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class FinancialMonthlyAggregateTargetConfiguration : IEntityTypeConfiguration<FinancialMonthlyAggregateTarget>
{
    public void Configure(EntityTypeBuilder<FinancialMonthlyAggregateTarget> b)
    {
        b.ToTable("financial_monthly_aggregate_targets", t => {
            t.HasCheckConstraint("ck_monthly_aggregate_amount", "amount >= 0 AND scale(amount) <= 2");
            t.HasCheckConstraint("ck_monthly_aggregate_competence", "EXTRACT(DAY FROM competence) = 1");
            t.HasCheckConstraint("ck_monthly_aggregate_type", "target_type IN ('FixedExpenses','CreditCards','TotalExpenses','RecoveryAmount')");
        });
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Amount).HasColumnType("numeric"); b.Property(x => x.Competence).HasColumnType("date");
        b.Property(x => x.TargetType).HasConversion<string>().HasMaxLength(30);
        b.HasIndex(x => new { x.WorkspaceId, x.Competence, x.TargetType }).IsUnique();
        b.HasOne<FinancialWorkspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class FinancialBootstrapProvenanceConfiguration : IEntityTypeConfiguration<FinancialBootstrapProvenance>
{
    public void Configure(EntityTypeBuilder<FinancialBootstrapProvenance> b)
    {
        b.ToTable("financial_bootstrap_provenance"); b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.SourceSystem).HasMaxLength(80); b.Property(x => x.SourceDocument).HasMaxLength(200);
        b.Property(x => x.ExternalReference).HasMaxLength(200); b.Property(x => x.ContentHash).HasMaxLength(64);
        b.Property(x => x.EntityType).HasMaxLength(100); b.Property(x => x.MetadataJson).HasColumnType("jsonb");
        b.HasIndex(x => new { x.WorkspaceId, x.SourceSystem, x.SourceDocument, x.ExternalReference }).IsUnique();
        b.HasOne<FinancialWorkspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
    }
}
