using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPluggyAutomationCenter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pluggy_automation_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sync_paused = table.Column<bool>(type: "boolean", nullable: false),
                    sync_interval_hours = table.Column<int>(type: "integer", nullable: true),
                    categorize_after_sync = table.Column<bool>(type: "boolean", nullable: false),
                    categorize_consent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pluggy_automation_settings", x => x.id);
                    table.CheckConstraint("ck_pluggy_automation_settings_consent", "(categorize_after_sync = FALSE) OR (categorize_consent_at IS NOT NULL)");
                    table.CheckConstraint("ck_pluggy_automation_settings_interval", "sync_interval_hours IS NULL OR sync_interval_hours IN (6, 12, 24)");
                    table.ForeignKey(
                        name: "fk_pluggy_automation_settings_financial_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "financial_workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "pluggy_categorization_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    trigger = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    sync_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    state = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    used_ai = table.Column<bool>(type: "boolean", nullable: false),
                    considered = table.Column<int>(type: "integer", nullable: false),
                    classified_local = table.Column<int>(type: "integer", nullable: false),
                    classified_ai = table.Column<int>(type: "integer", nullable: false),
                    pending = table.Column<int>(type: "integer", nullable: false),
                    skipped_changed = table.Column<int>(type: "integer", nullable: false),
                    ai_items_sent = table.Column<int>(type: "integer", nullable: false),
                    ai_calls = table.Column<int>(type: "integer", nullable: false),
                    ai_stopped_because = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    model = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pluggy_categorization_runs", x => x.id);
                    table.CheckConstraint("ck_pluggy_categorization_runs_counts", "considered >= 0 AND classified_local >= 0 AND classified_ai >= 0 AND pending >= 0 AND skipped_changed >= 0 AND ai_items_sent >= 0 AND ai_calls >= 0");
                    table.CheckConstraint("ck_pluggy_categorization_runs_finished", "(state = 'Running') = (finished_at IS NULL)");
                    table.CheckConstraint("ck_pluggy_categorization_runs_state", "state IN ('Running','Completed','CompletedWithPending','Failed')");
                    table.CheckConstraint("ck_pluggy_categorization_runs_trigger", "trigger IN ('Manual','AfterSync')");
                    table.ForeignKey(
                        name: "fk_pluggy_categorization_runs_financial_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "financial_workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_pluggy_automation_settings_workspace",
                table: "pluggy_automation_settings",
                column: "workspace_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pluggy_categorization_runs_workspace_id_started_at",
                table: "pluggy_categorization_runs",
                columns: new[] { "workspace_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ux_pluggy_categorization_runs_running",
                table: "pluggy_categorization_runs",
                column: "workspace_id",
                unique: true,
                filter: "state = 'Running'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pluggy_automation_settings");

            migrationBuilder.DropTable(
                name: "pluggy_categorization_runs");
        }
    }
}
