using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPluggySyncStateAndRunTrigger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "trigger",
                table: "pluggy_sync_runs",
                type: "character varying(12)",
                maxLength: 12,
                nullable: false,
                defaultValue: "Manual");

            migrationBuilder.CreateTable(
                name: "pluggy_sync_states",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_success_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_outcome = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    last_trigger = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true),
                    last_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    consecutive_failures = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    claimed_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    source_updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    source_status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    consent_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pluggy_sync_states", x => x.id);
                    table.CheckConstraint("ck_pluggy_sync_states_failures", "consecutive_failures >= 0");
                    table.CheckConstraint("ck_pluggy_sync_states_outcome", "last_outcome IN ('Never','Completed','CompletedWithErrors','Failed','ConsentExpired','SourceNotUpdated')");
                    table.CheckConstraint("ck_pluggy_sync_states_trigger", "last_trigger IS NULL OR last_trigger IN ('Manual','Scheduled')");
                    table.ForeignKey(
                        name: "fk_pluggy_sync_states_financial_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "financial_workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pluggy_sync_states_pluggy_item_links_connection_id",
                        column: x => x.connection_id,
                        principalTable: "pluggy_item_links",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_pluggy_sync_runs_trigger",
                table: "pluggy_sync_runs",
                sql: "\"trigger\" IN ('Manual','Scheduled')");

            migrationBuilder.CreateIndex(
                name: "ix_pluggy_sync_states_next_attempt_at",
                table: "pluggy_sync_states",
                column: "next_attempt_at");

            migrationBuilder.CreateIndex(
                name: "ix_pluggy_sync_states_workspace_id",
                table: "pluggy_sync_states",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "ux_pluggy_sync_states_connection",
                table: "pluggy_sync_states",
                column: "connection_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pluggy_sync_states");

            migrationBuilder.DropCheckConstraint(
                name: "ck_pluggy_sync_runs_trigger",
                table: "pluggy_sync_runs");

            migrationBuilder.DropColumn(
                name: "trigger",
                table: "pluggy_sync_runs");
        }
    }
}
