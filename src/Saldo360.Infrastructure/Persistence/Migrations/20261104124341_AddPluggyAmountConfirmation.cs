using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPluggyAmountConfirmation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_financial_planned_transactions_confirmed_triple",
                table: "financial_planned_transactions");

            migrationBuilder.AddColumn<string>(
                name: "amount_confirmation_source",
                table: "financial_planned_transactions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "amount_source_transaction_id",
                table: "financial_planned_transactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "financial_amount_confirmation_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recurrence_id = table.Column<Guid>(type: "uuid", nullable: true),
                    competence = table.Column<DateOnly>(type: "date", nullable: true),
                    planned_transaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    previous_amount = table.Column<decimal>(type: "numeric", nullable: true),
                    new_amount = table.Column<decimal>(type: "numeric", nullable: true),
                    source_transaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    details = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_amount_confirmation_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_financial_amount_confirmation_events_financial_workspaces_w",
                        column: x => x.workspace_id,
                        principalTable: "financial_workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_financial_planned_transactions_workspace_id_amount_source_t",
                table: "financial_planned_transactions",
                columns: new[] { "workspace_id", "amount_source_transaction_id" },
                filter: "amount_source_transaction_id IS NOT NULL AND NOT is_cancelled");

            migrationBuilder.AddCheckConstraint(
                name: "ck_financial_planned_transactions_confirmed_triple",
                table: "financial_planned_transactions",
                sql: "(confirmed_amount IS NULL AND amount_confirmed_at IS NULL AND amount_confirmed_by_user_id IS NULL AND amount_confirmation_source IS NULL AND amount_source_transaction_id IS NULL) OR (confirmed_amount IS NOT NULL AND amount_confirmed_at IS NOT NULL AND (amount_confirmed_by_user_id IS NOT NULL OR amount_confirmation_source = 'PluggyAuto') AND (amount_confirmation_source IN ('Manual', 'PluggyAuto', 'ManualLink') OR amount_confirmation_source IS NULL) AND (amount_source_transaction_id IS NOT NULL OR amount_confirmation_source IS NULL OR amount_confirmation_source = 'Manual'))");

            migrationBuilder.CreateIndex(
                name: "ix_financial_amount_confirmation_events_workspace_id_recurrenc",
                table: "financial_amount_confirmation_events",
                columns: new[] { "workspace_id", "recurrence_id", "competence", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_amount_confirmation_events_workspace_id_source_tr",
                table: "financial_amount_confirmation_events",
                columns: new[] { "workspace_id", "source_transaction_id", "kind" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "financial_amount_confirmation_events");

            migrationBuilder.DropIndex(
                name: "ix_financial_planned_transactions_workspace_id_amount_source_t",
                table: "financial_planned_transactions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_financial_planned_transactions_confirmed_triple",
                table: "financial_planned_transactions");

            migrationBuilder.DropColumn(
                name: "amount_confirmation_source",
                table: "financial_planned_transactions");

            migrationBuilder.DropColumn(
                name: "amount_source_transaction_id",
                table: "financial_planned_transactions");

            migrationBuilder.AddCheckConstraint(
                name: "ck_financial_planned_transactions_confirmed_triple",
                table: "financial_planned_transactions",
                sql: "(confirmed_amount IS NULL AND amount_confirmed_at IS NULL AND amount_confirmed_by_user_id IS NULL) OR (confirmed_amount IS NOT NULL AND amount_confirmed_at IS NOT NULL AND amount_confirmed_by_user_id IS NOT NULL)");
        }
    }
}
