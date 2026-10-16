using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlanningScenarios : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "financial_planning_scenarios",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    start_month = table.Column<DateOnly>(type: "date", nullable: false),
                    months = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_planning_scenarios", x => x.id);
                    table.UniqueConstraint("ak_financial_planning_scenarios_family_id_id", x => new { x.family_id, x.id });
                    table.CheckConstraint("ck_planning_scenarios_archived", "(status = 1) = (archived_at IS NOT NULL)");
                    table.CheckConstraint("ck_planning_scenarios_months", "months BETWEEN 1 AND 12");
                    table.CheckConstraint("ck_planning_scenarios_start_month", "extract(day from start_month) = 1");
                    table.ForeignKey(
                        name: "fk_financial_planning_scenarios_families_family_id",
                        column: x => x.family_id,
                        principalTable: "families",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "financial_planning_monthly_parameters",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scenario_id = table.Column<Guid>(type: "uuid", nullable: false),
                    competence = table.Column<DateOnly>(type: "date", nullable: false),
                    expected_additional_income = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    expected_additional_expense = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    contingency_reserve = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    recovery_target_percentage = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_planning_monthly_parameters", x => x.id);
                    table.CheckConstraint("ck_planning_parameters_competence", "extract(day from competence) = 1");
                    table.CheckConstraint("ck_planning_parameters_nonnegative", "expected_additional_income >= 0 AND expected_additional_expense >= 0 AND contingency_reserve >= 0");
                    table.CheckConstraint("ck_planning_parameters_notes_trimmed", "notes IS NULL OR octet_length(notes) = octet_length(btrim(notes))");
                    table.CheckConstraint("ck_planning_parameters_recovery", "recovery_target_percentage BETWEEN 0 AND 100");
                    table.ForeignKey(
                        name: "fk_financial_planning_monthly_parameters_financial_planning_sc",
                        columns: x => new { x.family_id, x.scenario_id },
                        principalTable: "financial_planning_scenarios",
                        principalColumns: new[] { "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_planning_parameters_scenario_competence",
                table: "financial_planning_monthly_parameters",
                columns: new[] { "family_id", "scenario_id", "competence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_financial_planning_scenarios_family_id_status_start_month",
                table: "financial_planning_scenarios",
                columns: new[] { "family_id", "status", "start_month" });

            migrationBuilder.CreateIndex(
                name: "ux_planning_scenarios_family_name",
                table: "financial_planning_scenarios",
                columns: new[] { "family_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "financial_planning_monthly_parameters");

            migrationBuilder.DropTable(
                name: "financial_planning_scenarios");
        }
    }
}
