using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Budgets;
using Saldo360.Domain.Families;
using Saldo360.Domain.Users;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class FinancialBudgetConfiguration : IEntityTypeConfiguration<FinancialBudget>
{
    public const string UniqueCompetenceIndex = "ux_financial_budgets_family_id_competence";

    public void Configure(EntityTypeBuilder<FinancialBudget> builder)
    {
        builder.ToTable("financial_budgets", table =>
        {
            table.HasCheckConstraint("ck_financial_budgets_competence_first_day", "extract(day from competence) = 1");
            table.HasCheckConstraint("ck_financial_budgets_description_trimmed",
                "description IS NULL OR octet_length(description) = octet_length(btrim(description))");
            table.HasCheckConstraint("ck_financial_budgets_archived",
                "is_archived = (archived_at IS NOT NULL) AND (archived_at IS NULL) = (archived_by_user_id IS NULL)");
        });
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).ValueGeneratedNever();
        builder.Property(item => item.Competence).HasColumnType("date");
        builder.Property(item => item.Description).HasMaxLength(FinancialBudget.DescriptionMaxLength);
        builder.HasAlternateKey(item => new { item.FamilyId, item.Id });
        builder.HasIndex(item => new { item.FamilyId, item.Competence }).IsUnique().HasDatabaseName(UniqueCompetenceIndex);
        builder.HasOne<Family>().WithMany().HasForeignKey(item => item.FamilyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(item => item.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(item => item.UpdatedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(item => item.ArchivedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
