using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Planning;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class FinancialPlanningMonthlyParameterConfiguration : IEntityTypeConfiguration<FinancialPlanningMonthlyParameter>
{
    public const string UniqueCompetenceIndex = "ux_planning_parameters_scenario_competence";
    public void Configure(EntityTypeBuilder<FinancialPlanningMonthlyParameter> builder)
    {
        builder.ToTable("financial_planning_monthly_parameters", table =>
        {
            table.HasCheckConstraint("ck_planning_parameters_competence", "extract(day from competence) = 1");
            table.HasCheckConstraint("ck_planning_parameters_nonnegative", "expected_additional_income >= 0 AND expected_additional_expense >= 0 AND contingency_reserve >= 0");
            table.HasCheckConstraint("ck_planning_parameters_recovery", "recovery_target_percentage BETWEEN 0 AND 100");
            table.HasCheckConstraint("ck_planning_parameters_notes_trimmed", "notes IS NULL OR octet_length(notes) = octet_length(btrim(notes))");
        });
        builder.HasKey(x => x.Id); builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Competence).HasColumnType("date");
        builder.Property(x => x.ExpectedAdditionalIncome).HasPrecision(18, 2);
        builder.Property(x => x.ExpectedAdditionalExpense).HasPrecision(18, 2);
        builder.Property(x => x.ContingencyReserve).HasPrecision(18, 2);
        builder.Property(x => x.RecoveryTargetPercentage).HasPrecision(5, 2);
        builder.Property(x => x.Notes).HasMaxLength(FinancialPlanningMonthlyParameter.NotesMaxLength);
        builder.HasIndex(x => new { x.WorkspaceId, x.ScenarioId, x.Competence }).IsUnique().HasDatabaseName(UniqueCompetenceIndex);
        builder.HasOne<FinancialPlanningScenario>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.ScenarioId }).HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}
