using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Workspaces;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class FinancialResetAuditConfiguration : IEntityTypeConfiguration<FinancialResetAudit>
{
    public void Configure(EntityTypeBuilder<FinancialResetAudit> b)
    {
        b.ToTable("financial_workspace_reset_audit"); b.HasKey(x => x.Id);
        b.Property(x => x.TraceId).HasMaxLength(128);
        b.HasOne<FinancialWorkspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.WorkspaceId, x.CreatedAt });
    }
}
internal sealed class FinancialResetFileCleanupConfiguration : IEntityTypeConfiguration<FinancialResetFileCleanup>
{
    public void Configure(EntityTypeBuilder<FinancialResetFileCleanup> b)
    {
        b.ToTable("financial_workspace_reset_file_cleanup"); b.HasKey(x => x.Id);
        b.Property(x => x.StorageReference).HasMaxLength(200);
        b.HasOne<FinancialWorkspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.WorkspaceId, x.StorageReference }).IsUnique();
    }
}
