using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFixedObligationConsolidation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "exception_competence",
                table: "financial_planned_transactions",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "exception_recurrence_id",
                table: "financial_planned_transactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "needs_historical_review",
                table: "financial_planned_transactions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "financial_obligation_settlements",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recurrence_id = table.Column<Guid>(type: "uuid", nullable: true),
                    competence = table.Column<DateOnly>(type: "date", nullable: false),
                    planned_transaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    financial_transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    origin = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    removed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_obligation_settlements", x => x.id);
                    table.CheckConstraint("ck_obligation_settlement_identity", "recurrence_id IS NOT NULL OR planned_transaction_id IS NOT NULL");
                    table.ForeignKey(
                        name: "fk_financial_obligation_settlements_financial_planned_transact",
                        columns: x => new { x.workspace_id, x.planned_transaction_id },
                        principalTable: "financial_planned_transactions",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_obligation_settlements_financial_recurrences_work",
                        columns: x => new { x.workspace_id, x.recurrence_id },
                        principalTable: "financial_recurrences",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_obligation_settlements_financial_transactions_wor",
                        columns: x => new { x.workspace_id, x.financial_transaction_id },
                        principalTable: "financial_transactions",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_obligation_settlements_financial_workspaces_works",
                        column: x => x.workspace_id,
                        principalTable: "financial_workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_financial_planned_transactions_recurrence_exception",
                table: "financial_planned_transactions",
                columns: new[] { "workspace_id", "exception_recurrence_id", "exception_competence" },
                unique: true,
                filter: "exception_recurrence_id IS NOT NULL AND NOT is_cancelled");

            migrationBuilder.AddCheckConstraint(
                name: "ck_financial_planned_transactions_exception_pair",
                table: "financial_planned_transactions",
                sql: "(exception_recurrence_id IS NULL AND exception_competence IS NULL) OR (exception_recurrence_id IS NOT NULL AND exception_competence IS NOT NULL AND EXTRACT(DAY FROM exception_competence) = 1)");

            migrationBuilder.CreateIndex(
                name: "ux_obligation_settlement_planned",
                table: "financial_obligation_settlements",
                columns: new[] { "workspace_id", "planned_transaction_id" },
                unique: true,
                filter: "removed_at IS NULL AND planned_transaction_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_obligation_settlement_recurrence_competence",
                table: "financial_obligation_settlements",
                columns: new[] { "workspace_id", "recurrence_id", "competence" },
                unique: true,
                filter: "removed_at IS NULL AND recurrence_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_obligation_settlement_transaction",
                table: "financial_obligation_settlements",
                columns: new[] { "workspace_id", "financial_transaction_id" },
                unique: true,
                filter: "removed_at IS NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_financial_planned_transactions_exception_recurrence",
                table: "financial_planned_transactions",
                columns: new[] { "workspace_id", "exception_recurrence_id" },
                principalTable: "financial_recurrences",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_financial_planned_transactions_exception_recurrence",
                table: "financial_planned_transactions");

            migrationBuilder.DropTable(
                name: "financial_obligation_settlements");

            migrationBuilder.DropIndex(
                name: "ux_financial_planned_transactions_recurrence_exception",
                table: "financial_planned_transactions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_financial_planned_transactions_exception_pair",
                table: "financial_planned_transactions");

            migrationBuilder.DropColumn(
                name: "exception_competence",
                table: "financial_planned_transactions");

            migrationBuilder.DropColumn(
                name: "exception_recurrence_id",
                table: "financial_planned_transactions");

            migrationBuilder.DropColumn(
                name: "needs_historical_review",
                table: "financial_planned_transactions");
        }
    }
}
