using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlannedTransfers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "financial_planned_transfers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    destination_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric", nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_cancelled = table.Column<bool>(type: "boolean", nullable: false),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    realized_transfer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    link_origin = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    linked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_planned_transfers", x => x.id);
                    table.CheckConstraint("ck_financial_planned_transfers_amount_positive", "amount > 0");
                    table.CheckConstraint("ck_financial_planned_transfers_amount_range", "amount < 10000000000000");
                    table.CheckConstraint("ck_financial_planned_transfers_amount_scale", "scale(amount) <= 2");
                    table.CheckConstraint("ck_financial_planned_transfers_cancelled_at", "is_cancelled = (cancelled_at IS NOT NULL)");
                    table.CheckConstraint("ck_financial_planned_transfers_currency", "currency = 'BRL'");
                    table.CheckConstraint("ck_financial_planned_transfers_description_not_blank", "btrim(description) <> ''");
                    table.CheckConstraint("ck_financial_planned_transfers_distinct_accounts", "source_account_id <> destination_account_id");
                    table.CheckConstraint("ck_financial_planned_transfers_link", "(realized_transfer_id IS NULL) = (link_origin IS NULL) AND (realized_transfer_id IS NULL) = (linked_at IS NULL)");
                    table.CheckConstraint("ck_financial_planned_transfers_state", "NOT (is_cancelled AND realized_transfer_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_financial_planned_transfers_application_users_cancelled_by_",
                        column: x => x.cancelled_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_planned_transfers_application_users_created_by_us",
                        column: x => x.created_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_planned_transfers_destination_account_same_family",
                        columns: x => new { x.workspace_id, x.destination_account_id },
                        principalTable: "financial_accounts",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_planned_transfers_financial_transfers_realized_tr",
                        column: x => x.realized_transfer_id,
                        principalTable: "financial_transfers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_planned_transfers_financial_workspaces_workspace_",
                        column: x => x.workspace_id,
                        principalTable: "financial_workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_planned_transfers_source_account_same_family",
                        columns: x => new { x.workspace_id, x.source_account_id },
                        principalTable: "financial_accounts",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_financial_planned_transfers_cancelled_by_user_id",
                table: "financial_planned_transfers",
                column: "cancelled_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_financial_planned_transfers_created_by_user_id",
                table: "financial_planned_transfers",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_financial_planned_transfers_realized_transfer_id",
                table: "financial_planned_transfers",
                column: "realized_transfer_id",
                unique: true,
                filter: "realized_transfer_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_financial_planned_transfers_workspace_id_destination_accoun",
                table: "financial_planned_transfers",
                columns: new[] { "workspace_id", "destination_account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_planned_transfers_workspace_id_due_date_id",
                table: "financial_planned_transfers",
                columns: new[] { "workspace_id", "due_date", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_planned_transfers_workspace_id_source_account_id",
                table: "financial_planned_transfers",
                columns: new[] { "workspace_id", "source_account_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "financial_planned_transfers");
        }
    }
}
