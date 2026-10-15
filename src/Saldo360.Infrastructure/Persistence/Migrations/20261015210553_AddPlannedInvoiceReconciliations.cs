using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlannedInvoiceReconciliations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "financial_planned_invoice_reconciliations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    planned_transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    credit_card_invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    removed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    removed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_planned_invoice_reconciliations", x => x.id);
                    table.CheckConstraint("ck_financial_planned_invoice_reconciliations_removed_pair", "(removed_at IS NULL) = (removed_by_user_id IS NULL)");
                    table.ForeignKey(
                        name: "fk_financial_planned_invoice_reconciliations_application_users",
                        column: x => x.created_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_planned_invoice_reconciliations_application_users1",
                        column: x => x.removed_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_planned_invoice_reconciliations_families_family_id",
                        column: x => x.family_id,
                        principalTable: "families",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_reconciliations_invoices_same_family",
                        columns: x => new { x.family_id, x.credit_card_invoice_id },
                        principalTable: "financial_credit_card_invoices",
                        principalColumns: new[] { "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_reconciliations_planned_same_family",
                        columns: x => new { x.family_id, x.planned_transaction_id },
                        principalTable: "financial_planned_transactions",
                        principalColumns: new[] { "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_financial_planned_invoice_reconciliations_created_by_user_id",
                table: "financial_planned_invoice_reconciliations",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_financial_planned_invoice_reconciliations_family_id_created",
                table: "financial_planned_invoice_reconciliations",
                columns: new[] { "family_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_planned_invoice_reconciliations_family_id_credit_",
                table: "financial_planned_invoice_reconciliations",
                columns: new[] { "family_id", "credit_card_invoice_id" },
                unique: true,
                filter: "removed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_financial_planned_invoice_reconciliations_family_id_planned",
                table: "financial_planned_invoice_reconciliations",
                columns: new[] { "family_id", "planned_transaction_id" },
                unique: true,
                filter: "removed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_financial_planned_invoice_reconciliations_removed_by_user_id",
                table: "financial_planned_invoice_reconciliations",
                column: "removed_by_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "financial_planned_invoice_reconciliations");
        }
    }
}
