using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.CreditCards;
using Saldo360.Domain.Workspaces;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class FinancialEmailCollectorCheckpointConfiguration : IEntityTypeConfiguration<FinancialEmailCollectorCheckpoint>
{
    public void Configure(EntityTypeBuilder<FinancialEmailCollectorCheckpoint> builder)
    {
        builder.ToTable("financial_email_collector_checkpoints");
        builder.HasKey(item => new { item.WorkspaceId, item.Id });
        builder.Property(item => item.ProfileName).HasMaxLength(100).IsRequired();
        builder.Property(item => item.LastResult).HasMaxLength(40);
        builder.HasIndex(item => new { item.WorkspaceId, item.ProfileName }).IsUnique();
        builder.HasOne<FinancialWorkspace>().WithMany().HasForeignKey(item => item.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
    }
}
