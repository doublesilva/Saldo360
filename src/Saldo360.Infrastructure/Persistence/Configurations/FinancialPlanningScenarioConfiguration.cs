using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Families;
using Saldo360.Domain.Planning;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class FinancialPlanningScenarioConfiguration : IEntityTypeConfiguration<FinancialPlanningScenario>
{
    public const string UniqueNameIndex = "ux_planning_scenarios_family_name";
    public void Configure(EntityTypeBuilder<FinancialPlanningScenario> builder)
    {
        builder.ToTable("financial_planning_scenarios", table =>
        {
            table.HasCheckConstraint("ck_planning_scenarios_months", "months BETWEEN 1 AND 12");
            table.HasCheckConstraint("ck_planning_scenarios_start_month", "extract(day from start_month) = 1");
            table.HasCheckConstraint("ck_planning_scenarios_archived", "(status = 1) = (archived_at IS NOT NULL)");
        });
        builder.HasKey(x => x.Id); builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Name).HasMaxLength(FinancialPlanningScenario.NameMaxLength).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(FinancialPlanningScenario.DescriptionMaxLength);
        builder.Property(x => x.StartMonth).HasColumnType("date");
        builder.Property(x => x.Status).HasConversion<int>();
        builder.HasAlternateKey(x => new { x.FamilyId, x.Id });
        builder.HasIndex(x => new { x.FamilyId, x.Status, x.StartMonth });
        builder.HasIndex(x => new { x.FamilyId, x.Name }).IsUnique().HasDatabaseName(UniqueNameIndex);
        builder.HasOne<Family>().WithMany().HasForeignKey(x => x.FamilyId).OnDelete(DeleteBehavior.Restrict);
    }
}
