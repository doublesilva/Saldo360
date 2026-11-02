using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIncomeForecast : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "financial_income_forecast_months",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    competence = table.Column<DateOnly>(type: "date", nullable: false),
                    billable_days = table.Column<int>(type: "integer", nullable: true),
                    expected_amount = table.Column<decimal>(type: "numeric", nullable: true),
                    payment_date = table.Column<DateOnly>(type: "date", nullable: true),
                    is_dismissed = table.Column<bool>(type: "boolean", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_income_forecast_months", x => x.id);
                    table.CheckConstraint("ck_income_forecast_month_competence", "EXTRACT(DAY FROM competence) = 1");
                    table.CheckConstraint("ck_income_forecast_month_values", "(billable_days IS NULL OR billable_days BETWEEN 0 AND 31) AND (expected_amount IS NULL OR (expected_amount >= 0 AND scale(expected_amount) <= 2))");
                    table.ForeignKey(
                        name: "fk_financial_income_forecast_months_financial_workspaces_works",
                        column: x => x.workspace_id,
                        principalTable: "financial_workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "financial_income_forecasts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    hourly_rate = table.Column<decimal>(type: "numeric", nullable: false),
                    hours_per_day = table.Column<decimal>(type: "numeric", nullable: false),
                    payment_day = table.Column<int>(type: "integer", nullable: false),
                    payment_offset_months = table.Column<int>(type: "integer", nullable: false),
                    tax_reserve_percent = table.Column<decimal>(type: "numeric", nullable: false),
                    operational_reserve = table.Column<decimal>(type: "numeric", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_income_forecasts", x => x.id);
                    table.CheckConstraint("ck_income_forecast_hours", "hours_per_day > 0 AND hours_per_day <= 24");
                    table.CheckConstraint("ck_income_forecast_payment", "payment_day BETWEEN 1 AND 31 AND payment_offset_months BETWEEN 0 AND 3");
                    table.CheckConstraint("ck_income_forecast_rate", "hourly_rate > 0 AND scale(hourly_rate) <= 2");
                    table.CheckConstraint("ck_income_forecast_reserves", "tax_reserve_percent BETWEEN 0 AND 100 AND operational_reserve >= 0 AND scale(operational_reserve) <= 2");
                    table.ForeignKey(
                        name: "fk_financial_income_forecasts_financial_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "financial_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_income_forecasts_financial_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "financial_workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_income_forecast_months_workspace_competence",
                table: "financial_income_forecast_months",
                columns: new[] { "workspace_id", "competence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_financial_income_forecasts_account_id",
                table: "financial_income_forecasts",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ux_income_forecasts_workspace",
                table: "financial_income_forecasts",
                column: "workspace_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "financial_income_forecast_months");

            migrationBuilder.DropTable(
                name: "financial_income_forecasts");
        }
    }
}
