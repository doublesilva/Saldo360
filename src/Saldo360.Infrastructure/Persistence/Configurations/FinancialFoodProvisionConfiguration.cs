using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Planning;
using Saldo360.Domain.Workspaces;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class FinancialFoodProvisionConfiguration : IEntityTypeConfiguration<FinancialFoodProvision>
{
    public void Configure(EntityTypeBuilder<FinancialFoodProvision> b)
    {
        b.ToTable("financial_food_provisions", t =>
        {
            t.HasCheckConstraint("ck_food_provision_competence", "competence IS NULL OR EXTRACT(DAY FROM competence) = 1");
            t.HasCheckConstraint("ck_food_provision_amount", "monthly_amount >= 0 AND scale(monthly_amount) <= 2");
        });
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Competence).HasColumnType("date");
        b.Property(x => x.MonthlyAmount).HasColumnType("numeric");
        b.HasIndex(x => x.WorkspaceId).IsUnique().HasFilter("competence IS NULL").HasDatabaseName("ux_food_provisions_workspace_default");
        b.HasIndex(x => new { x.WorkspaceId, x.Competence }).IsUnique().HasFilter("competence IS NOT NULL").HasDatabaseName("ux_food_provisions_workspace_competence");
        b.HasOne<FinancialWorkspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
    }
}
