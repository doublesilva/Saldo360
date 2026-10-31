using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Workspaces;
using Saldo360.Domain.Users;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class FinancialWorkspaceConfiguration : IEntityTypeConfiguration<FinancialWorkspace>
{
    public void Configure(EntityTypeBuilder<FinancialWorkspace> b)
    {
        b.ToTable("financial_workspaces", t =>
        {
            t.HasCheckConstraint("ck_workspace_type", "type IN ('Personal', 'Shared')");
            // Registro da inicialização do catálogo padrão: data e versão andam juntas.
            t.HasCheckConstraint("ck_workspace_default_categories_initialization", "(default_categories_initialized_at IS NULL) = (default_categories_catalog_version IS NULL)");
        });
        b.HasKey(x => x.Id);
        b.HasAlternateKey(x => new { x.Id, x.OwnerUserId });
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(160).IsRequired();
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Version).IsRowVersion();
        b.HasIndex(x => x.OwnerUserId).IsUnique().HasFilter("type = 'Personal'");
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
