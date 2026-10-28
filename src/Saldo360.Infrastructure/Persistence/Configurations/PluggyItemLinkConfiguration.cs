using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Pluggy;
using Saldo360.Domain.Users;
using Saldo360.Domain.Workspaces;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class PluggyItemLinkConfiguration : IEntityTypeConfiguration<PluggyItemLink>
{
    public void Configure(EntityTypeBuilder<PluggyItemLink> b)
    {
        b.ToTable("pluggy_item_links", t => t.HasCheckConstraint("ck_pluggy_item_links_item_id", "pluggy_item_id ~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'"));
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.PluggyItemId).HasMaxLength(36).IsRequired();
        b.Property(x => x.Label).HasMaxLength(PluggyItemLink.LabelMaxLength);
        // Um Item da Pluggy pertence a um único vínculo: outro usuário não consegue reaproveitá-lo.
        b.HasIndex(x => x.PluggyItemId).IsUnique().HasDatabaseName("ux_pluggy_item_links_item");
        b.HasIndex(x => new { x.WorkspaceId, x.CreatedAt });
        b.HasOne<FinancialWorkspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}
