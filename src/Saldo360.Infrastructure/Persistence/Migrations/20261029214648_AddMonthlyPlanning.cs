using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMonthlyPlanning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "financial_non_working_days",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    date = table.Column<DateOnly>(type: "date", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    description = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_non_working_days", x => x.id);
                    table.CheckConstraint("ck_non_working_day_kind", "kind IN ('Holiday','NonWorking')");
                    table.ForeignKey(
                        name: "fk_financial_non_working_days_financial_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "financial_workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "financial_plan_history",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    competence = table.Column<DateOnly>(type: "date", nullable: false),
                    subject = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    previous_amount = table.Column<decimal>(type: "numeric", nullable: true),
                    new_amount = table.Column<decimal>(type: "numeric", nullable: true),
                    note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_plan_history", x => x.id);
                    table.CheckConstraint("ck_plan_history_action", "action IN ('Created','Revised','Removed','Restored','Copied')");
                    table.CheckConstraint("ck_plan_history_competence", "EXTRACT(DAY FROM competence) = 1");
                    table.CheckConstraint("ck_plan_history_subject", "subject IN ('Line','CategoryLimit')");
                    table.ForeignKey(
                        name: "fk_financial_plan_history_financial_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "financial_workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "financial_plan_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    competence = table.Column<DateOnly>(type: "date", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    nature = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    destination_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    original_amount = table.Column<decimal>(type: "numeric", nullable: false),
                    amount = table.Column<decimal>(type: "numeric", nullable: false),
                    expected_date = table.Column<DateOnly>(type: "date", nullable: true),
                    hourly_rate = table.Column<decimal>(type: "numeric", nullable: true),
                    hours_per_day = table.Column<decimal>(type: "numeric", nullable: true),
                    billable_days = table.Column<int>(type: "integer", nullable: true),
                    copied_from_line_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    archived_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_plan_lines", x => x.id);
                    table.CheckConstraint("ck_plan_line_amounts", "amount > 0 AND scale(amount) <= 2 AND original_amount > 0 AND scale(original_amount) <= 2");
                    table.CheckConstraint("ck_plan_line_competence", "EXTRACT(DAY FROM competence) = 1");
                    table.CheckConstraint("ck_plan_line_expected_date", "expected_date IS NULL OR date_trunc('month', expected_date)::date = competence");
                    table.CheckConstraint("ck_plan_line_hourly", "(hourly_rate IS NULL AND hours_per_day IS NULL AND billable_days IS NULL) OR (kind = 'Income' AND hourly_rate > 0 AND hours_per_day > 0 AND hours_per_day <= 24 AND billable_days > 0)");
                    table.CheckConstraint("ck_plan_line_kind", "kind IN ('Income','Expense','Transfer')");
                    table.CheckConstraint("ck_plan_line_nature", "nature IS NULL OR nature IN ('Recurring','Fixed','Variable','Extraordinary','Reserve')");
                    table.CheckConstraint("ck_plan_line_reserve", "nature IS DISTINCT FROM 'Reserve' OR kind = 'Expense'");
                    table.CheckConstraint("ck_plan_line_transfer", "(kind = 'Transfer' AND account_id IS NOT NULL AND destination_account_id IS NOT NULL AND account_id <> destination_account_id AND nature IS NULL AND category_id IS NULL) OR (kind <> 'Transfer' AND destination_account_id IS NULL AND nature IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_financial_plan_lines_financial_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "financial_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_plan_lines_financial_accounts_destination_account",
                        column: x => x.destination_account_id,
                        principalTable: "financial_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_plan_lines_financial_categories_category_id",
                        column: x => x.category_id,
                        principalTable: "financial_categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_plan_lines_financial_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "financial_workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_non_working_days_workspace_date",
                table: "financial_non_working_days",
                columns: new[] { "workspace_id", "date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_plan_history_workspace_competence_at",
                table: "financial_plan_history",
                columns: new[] { "workspace_id", "competence", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_plan_history_workspace_subject",
                table: "financial_plan_history",
                columns: new[] { "workspace_id", "subject_id" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_plan_lines_account_id",
                table: "financial_plan_lines",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_financial_plan_lines_category_id",
                table: "financial_plan_lines",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_financial_plan_lines_destination_account_id",
                table: "financial_plan_lines",
                column: "destination_account_id");

            migrationBuilder.CreateIndex(
                name: "ix_plan_lines_workspace_competence",
                table: "financial_plan_lines",
                columns: new[] { "workspace_id", "competence" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "financial_non_working_days");

            migrationBuilder.DropTable(
                name: "financial_plan_history");

            migrationBuilder.DropTable(
                name: "financial_plan_lines");
        }
    }
}
