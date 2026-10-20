using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Budgets;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;
using Saldo360.Domain.Users;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class FinancialBudgetTargetConfiguration : IEntityTypeConfiguration<FinancialBudgetTarget>
{
    public const string UniqueGroupingIndex = "ux_financial_budget_targets_budget_grouping_category";

    public void Configure(EntityTypeBuilder<FinancialBudgetTarget> builder)
    {
        builder.ToTable("financial_budget_targets", table =>
        {
            table.HasCheckConstraint("ck_financial_budget_targets_grouping_type", "grouping_type = 'Category'");
            table.HasCheckConstraint("ck_financial_budget_targets_category_type", "category_type = 'Expense'");
            table.HasCheckConstraint("ck_financial_budget_targets_name_not_blank", "btrim(name) <> ''");
            table.HasCheckConstraint("ck_financial_budget_targets_name_trimmed", "octet_length(name) = octet_length(btrim(name))");
            table.HasCheckConstraint("ck_financial_budget_targets_amount_nonnegative", "planned_amount >= 0");
            table.HasCheckConstraint("ck_financial_budget_targets_amount_scale", $"scale(planned_amount) <= {Money.MaxScale}");
            table.HasCheckConstraint("ck_financial_budget_targets_amount_range", $"planned_amount < {Money.Limit}");
            table.HasCheckConstraint("ck_financial_budget_targets_archived",
                "is_archived = (archived_at IS NOT NULL) AND (archived_at IS NULL) = (archived_by_user_id IS NULL)");
        });
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).ValueGeneratedNever();
        builder.Property(item => item.Name).HasMaxLength(FinancialBudgetTarget.NameMaxLength);
        builder.Property(item => item.PlannedAmount).HasColumnType("numeric");
        builder.Property(item => item.GroupingType).HasConversion<string>().HasMaxLength(20);
        builder.Property(item => item.CategoryType).HasConversion<string>().HasMaxLength(20);
        builder.HasIndex(item => new { item.BudgetId, item.GroupingType, item.CategoryId })
            .IsUnique().HasDatabaseName(UniqueGroupingIndex);
        builder.HasOne<FinancialBudget>().WithMany()
            .HasForeignKey(item => new { item.WorkspaceId, item.BudgetId })
            .HasPrincipalKey(item => new { item.WorkspaceId, item.Id })
            .HasConstraintName("fk_financial_budget_targets_budgets_same_family")
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<FinancialCategory>().WithMany()
            .HasForeignKey(item => new { item.WorkspaceId, item.CategoryId, item.CategoryType })
            .HasPrincipalKey(item => new { item.WorkspaceId, item.Id, item.Type })
            .HasConstraintName("fk_financial_budget_targets_categories_same_family_expense")
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(item => item.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(item => item.UpdatedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(item => item.ArchivedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
