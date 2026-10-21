using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFinancialWorkspaceResetCoordination : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_financial_reset_at",
                table: "financial_workspaces",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "financial_workspace_reset_audit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    trace_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    deleted_entities = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_workspace_reset_audit", x => x.id);
                    table.ForeignKey(
                        name: "fk_financial_workspace_reset_audit_financial_workspaces_worksp",
                        column: x => x.workspace_id,
                        principalTable: "financial_workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "financial_workspace_reset_file_cleanup",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    storage_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_workspace_reset_file_cleanup", x => x.id);
                    table.ForeignKey(
                        name: "fk_financial_workspace_reset_file_cleanup_financial_workspaces",
                        column: x => x.workspace_id,
                        principalTable: "financial_workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_financial_workspace_reset_audit_workspace_id_created_at",
                table: "financial_workspace_reset_audit",
                columns: new[] { "workspace_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_workspace_reset_file_cleanup_workspace_id_storage",
                table: "financial_workspace_reset_file_cleanup",
                columns: new[] { "workspace_id", "storage_reference" },
                unique: true);
            migrationBuilder.Sql("""
                CREATE FUNCTION saldo360_financial_write_guard() RETURNS trigger LANGUAGE plpgsql AS $guard$
                DECLARE w uuid; reset_at timestamptz; root_version bigint; expected_version text; started timestamptz;
                BEGIN
                    IF TG_OP = 'DELETE' THEN w := OLD.workspace_id; ELSE w := NEW.workspace_id; END IF;
                    IF TG_OP = 'UPDATE' AND OLD.workspace_id <> NEW.workspace_id THEN
                        RAISE EXCEPTION 'Financial scope is immutable' USING ERRCODE='23503';
                    END IF;
                    SELECT x.xmin::text::bigint, x.last_financial_reset_at INTO root_version, reset_at
                    FROM financial_workspaces x WHERE x.id = w FOR UPDATE;
                    IF NOT FOUND THEN
                        IF TG_OP = 'DELETE' THEN RETURN OLD; ELSE RETURN NEW; END IF;
                    END IF;
                    IF current_setting('saldo360.resetting_workspace', true) IS DISTINCT FROM w::text THEN
                        expected_version := NULLIF(current_setting('saldo360.writer_workspace_version', true), '');
                        started := COALESCE(NULLIF(current_setting('saldo360.writer_started_at', true), '')::timestamptz, transaction_timestamp());
                        IF (expected_version IS NOT NULL AND root_version <> expected_version::bigint)
                           OR (reset_at IS NOT NULL AND started <= reset_at) THEN
                            RAISE EXCEPTION 'FINANCIAL_WORKSPACE_RESET_CONFLICT';
                        END IF;
                    END IF;
                    IF TG_OP = 'DELETE' THEN RETURN OLD; ELSE RETURN NEW; END IF;
                END $guard$;
                DO $install$
                DECLARE t text;
                BEGIN
                    FOR t IN SELECT table_name FROM information_schema.columns
                        WHERE table_schema='public' AND column_name='workspace_id'
                          AND (table_name LIKE 'financial_%' OR table_name='service_account_bindings')
                          AND table_name NOT IN ('financial_workspace_reset_audit','financial_workspace_reset_file_cleanup')
                    LOOP
                        EXECUTE format('CREATE TRIGGER saldo360_financial_write_guard BEFORE INSERT OR UPDATE OR DELETE ON %I FOR EACH ROW EXECUTE FUNCTION saldo360_financial_write_guard()', t);
                    END LOOP;
                END $install$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $preserve$
                BEGIN
                    IF EXISTS (SELECT 1 FROM financial_workspace_reset_file_cleanup)
                       OR EXISTS (SELECT 1 FROM financial_workspace_reset_audit)
                       OR EXISTS (SELECT 1 FROM financial_workspaces WHERE last_financial_reset_at IS NOT NULL) THEN
                        RAISE EXCEPTION 'FINANCIAL_RESET_ROLLBACK_REQUIRES_RECOVERY_PLAN';
                    END IF;
                END $preserve$;
                DO $uninstall$
                DECLARE t text;
                BEGIN
                    FOR t IN SELECT event_object_table FROM information_schema.triggers
                        WHERE trigger_schema='public' AND trigger_name='saldo360_financial_write_guard'
                        GROUP BY event_object_table
                    LOOP EXECUTE format('DROP TRIGGER saldo360_financial_write_guard ON %I', t); END LOOP;
                END $uninstall$;
                DROP FUNCTION saldo360_financial_write_guard();
                """);
            migrationBuilder.DropTable(
                name: "financial_workspace_reset_audit");

            migrationBuilder.DropTable(
                name: "financial_workspace_reset_file_cleanup");

            migrationBuilder.DropColumn(
                name: "last_financial_reset_at",
                table: "financial_workspaces");
        }
    }
}
