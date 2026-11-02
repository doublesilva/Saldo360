using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Accounts;
using Saldo360.Domain.Planning;
using Saldo360.Domain.Workspaces;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class FinancialIncomeForecastConfiguration : IEntityTypeConfiguration<FinancialIncomeForecast>
{
    public void Configure(EntityTypeBuilder<FinancialIncomeForecast> b)
    {
        b.ToTable("financial_income_forecasts", t =>
        {
            t.HasCheckConstraint("ck_income_forecast_rate", "hourly_rate > 0 AND scale(hourly_rate) <= 2");
            t.HasCheckConstraint("ck_income_forecast_hours", "hours_per_day > 0 AND hours_per_day <= 24");
            t.HasCheckConstraint("ck_income_forecast_payment", "payment_day BETWEEN 1 AND 31 AND payment_offset_months BETWEEN 0 AND 3");
            t.HasCheckConstraint("ck_income_forecast_reserves", "tax_reserve_percent BETWEEN 0 AND 100 AND operational_reserve >= 0 AND scale(operational_reserve) <= 2");
        });
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.HourlyRate).HasColumnType("numeric");
        b.Property(x => x.HoursPerDay).HasColumnType("numeric");
        b.Property(x => x.TaxReservePercent).HasColumnType("numeric");
        b.Property(x => x.OperationalReserve).HasColumnType("numeric");
        b.HasIndex(x => x.WorkspaceId).IsUnique().HasDatabaseName("ux_income_forecasts_workspace");
        b.HasOne<FinancialWorkspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<FinancialAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class FinancialIncomeForecastMonthConfiguration : IEntityTypeConfiguration<FinancialIncomeForecastMonth>
{
    public void Configure(EntityTypeBuilder<FinancialIncomeForecastMonth> b)
    {
        b.ToTable("financial_income_forecast_months", t =>
        {
            t.HasCheckConstraint("ck_income_forecast_month_competence", "EXTRACT(DAY FROM competence) = 1");
            t.HasCheckConstraint("ck_income_forecast_month_values", "(billable_days IS NULL OR billable_days BETWEEN 0 AND 31) AND (expected_amount IS NULL OR (expected_amount >= 0 AND scale(expected_amount) <= 2))");
        });
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Competence).HasColumnType("date");
        b.Property(x => x.PaymentDate).HasColumnType("date");
        b.Property(x => x.ExpectedAmount).HasColumnType("numeric");
        b.HasIndex(x => new { x.WorkspaceId, x.Competence }).IsUnique().HasDatabaseName("ux_income_forecast_months_workspace_competence");
        b.HasOne<FinancialWorkspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
    }
}
