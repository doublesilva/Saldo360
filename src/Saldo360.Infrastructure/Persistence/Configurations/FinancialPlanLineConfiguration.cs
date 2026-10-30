using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Planning;
using Saldo360.Domain.Workspaces;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class FinancialPlanLineConfiguration : IEntityTypeConfiguration<FinancialPlanLine>
{
    public void Configure(EntityTypeBuilder<FinancialPlanLine> b)
    {
        b.ToTable("financial_plan_lines", t =>
        {
            t.HasCheckConstraint("ck_plan_line_competence", "EXTRACT(DAY FROM competence) = 1");
            t.HasCheckConstraint("ck_plan_line_amounts", "amount > 0 AND scale(amount) <= 2 AND original_amount > 0 AND scale(original_amount) <= 2");
            t.HasCheckConstraint("ck_plan_line_kind", "kind IN ('Income','Expense','Transfer')");
            t.HasCheckConstraint("ck_plan_line_nature", "nature IS NULL OR nature IN ('Recurring','Fixed','Variable','Extraordinary','Reserve')");
            t.HasCheckConstraint("ck_plan_line_transfer", "(kind = 'Transfer' AND account_id IS NOT NULL AND destination_account_id IS NOT NULL AND account_id <> destination_account_id AND nature IS NULL AND category_id IS NULL)"
                + " OR (kind <> 'Transfer' AND destination_account_id IS NULL AND nature IS NOT NULL)");
            t.HasCheckConstraint("ck_plan_line_reserve", "nature IS DISTINCT FROM 'Reserve' OR kind = 'Expense'");
            t.HasCheckConstraint("ck_plan_line_hourly", "(hourly_rate IS NULL AND hours_per_day IS NULL AND billable_days IS NULL)"
                + " OR (kind = 'Income' AND hourly_rate > 0 AND hours_per_day > 0 AND hours_per_day <= 24 AND billable_days > 0)");
            t.HasCheckConstraint("ck_plan_line_expected_date", "expected_date IS NULL OR date_trunc('month', expected_date)::date = competence");
        });
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Competence).HasColumnType("date");
        b.Property(x => x.ExpectedDate).HasColumnType("date");
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Nature).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Description).HasMaxLength(FinancialPlanLine.DescriptionMaxLength);
        b.Property(x => x.OriginalAmount).HasColumnType("numeric");
        b.Property(x => x.Amount).HasColumnType("numeric");
        b.Property(x => x.HourlyRate).HasColumnType("numeric");
        b.Property(x => x.HoursPerDay).HasColumnType("numeric");
        b.Ignore(x => x.IsHourly);
        b.HasIndex(x => new { x.WorkspaceId, x.Competence }).HasDatabaseName("ix_plan_lines_workspace_competence");
        b.HasOne<FinancialWorkspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FinancialAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FinancialAccount>().WithMany().HasForeignKey(x => x.DestinationAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FinancialCategory>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class FinancialPlanHistoryEntryConfiguration : IEntityTypeConfiguration<FinancialPlanHistoryEntry>
{
    public void Configure(EntityTypeBuilder<FinancialPlanHistoryEntry> b)
    {
        b.ToTable("financial_plan_history", t =>
        {
            t.HasCheckConstraint("ck_plan_history_competence", "EXTRACT(DAY FROM competence) = 1");
            t.HasCheckConstraint("ck_plan_history_subject", "subject IN ('Line','CategoryLimit')");
            t.HasCheckConstraint("ck_plan_history_action", "action IN ('Created','Revised','Removed','Restored','Copied')");
        });
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Competence).HasColumnType("date");
        b.Property(x => x.Subject).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Action).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Label).HasMaxLength(FinancialPlanHistoryEntry.LabelMaxLength);
        b.Property(x => x.Note).HasMaxLength(FinancialPlanHistoryEntry.NoteMaxLength);
        b.Property(x => x.PreviousAmount).HasColumnType("numeric");
        b.Property(x => x.NewAmount).HasColumnType("numeric");
        b.HasIndex(x => new { x.WorkspaceId, x.Competence, x.At }).HasDatabaseName("ix_plan_history_workspace_competence_at");
        b.HasIndex(x => new { x.WorkspaceId, x.SubjectId }).HasDatabaseName("ix_plan_history_workspace_subject");
        b.HasOne<FinancialWorkspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class FinancialNonWorkingDayConfiguration : IEntityTypeConfiguration<FinancialNonWorkingDay>
{
    public void Configure(EntityTypeBuilder<FinancialNonWorkingDay> b)
    {
        b.ToTable("financial_non_working_days", t => t.HasCheckConstraint("ck_non_working_day_kind", "kind IN ('Holiday','NonWorking')"));
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Date).HasColumnType("date");
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Description).HasMaxLength(FinancialNonWorkingDay.DescriptionMaxLength);
        b.HasIndex(x => new { x.WorkspaceId, x.Date }).IsUnique().HasDatabaseName("ux_non_working_days_workspace_date");
        b.HasOne<FinancialWorkspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
    }
}
