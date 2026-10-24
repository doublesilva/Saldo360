using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOfficialInvoiceConsolidation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_invoice_adjustments_type",
                table: "financial_credit_card_invoice_adjustments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_invoice_adjustments_type_nature",
                table: "financial_credit_card_invoice_adjustments");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "official_closed_at",
                table: "financial_credit_card_invoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "official_total",
                table: "financial_credit_card_invoices",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "official_version",
                table: "financial_credit_card_invoices",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "official_category_id",
                table: "financial_credit_card_installments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "financial_credit_card_invoice_consolidations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    credit_card_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    document_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    decision_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    state_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    snapshot_json = table.Column<string>(type: "jsonb", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    official_total = table.Column<decimal>(type: "numeric", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_credit_card_invoice_consolidations", x => x.id);
                    table.CheckConstraint("ck_official_consolidation_total", "official_total >= 0 AND scale(official_total) <= 2 AND version > 0");
                    table.ForeignKey(
                        name: "fk_financial_credit_card_invoice_consolidations_application_us",
                        column: x => x.created_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_credit_card_invoice_consolidations_financial_cred",
                        columns: x => new { x.workspace_id, x.credit_card_id },
                        principalTable: "financial_credit_cards",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_credit_card_invoice_consolidations_financial_cred1",
                        columns: x => new { x.workspace_id, x.invoice_id },
                        principalTable: "financial_credit_card_invoices",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_credit_card_invoice_consolidations_financial_docu",
                        columns: x => new { x.workspace_id, x.document_id },
                        principalTable: "financial_document_inbox_items",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_invoice_official_state",
                table: "financial_credit_card_invoices",
                sql: "(official_version = 0 AND official_total IS NULL AND official_closed_at IS NULL) OR (official_version > 0 AND official_total >= 0 AND scale(official_total) <= 2 AND official_closed_at IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_invoice_adjustments_type",
                table: "financial_credit_card_invoice_adjustments",
                sql: "type IN ('Fee', 'Credit', 'Refund', 'PreviousBalance', 'FinancedBalance')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_invoice_adjustments_type_nature",
                table: "financial_credit_card_invoice_adjustments",
                sql: "(type IN ('Fee', 'PreviousBalance', 'FinancedBalance') AND nature = 'Debit') OR (type IN ('Credit', 'Refund') AND nature = 'Credit')");

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_card_installments_workspace_id_official_ca",
                table: "financial_credit_card_installments",
                columns: new[] { "workspace_id", "official_category_id" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_card_invoice_consolidations_created_by_use",
                table: "financial_credit_card_invoice_consolidations",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_card_invoice_consolidations_workspace_id_c",
                table: "financial_credit_card_invoice_consolidations",
                columns: new[] { "workspace_id", "credit_card_id", "document_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_card_invoice_consolidations_workspace_id_d",
                table: "financial_credit_card_invoice_consolidations",
                columns: new[] { "workspace_id", "document_id" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_card_invoice_consolidations_workspace_id_i",
                table: "financial_credit_card_invoice_consolidations",
                columns: new[] { "workspace_id", "invoice_id", "version" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_credit_card_installments_financial_categories_wor",
                table: "financial_credit_card_installments",
                columns: new[] { "workspace_id", "official_category_id" },
                principalTable: "financial_categories",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Restrict);
            migrationBuilder.Sql(OfficialInvoiceGuards.Up);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(OfficialInvoiceGuards.Down);
            migrationBuilder.DropForeignKey(
                name: "fk_financial_credit_card_installments_financial_categories_wor",
                table: "financial_credit_card_installments");

            migrationBuilder.DropTable(
                name: "financial_credit_card_invoice_consolidations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_invoice_official_state",
                table: "financial_credit_card_invoices");

            migrationBuilder.DropCheckConstraint(
                name: "ck_invoice_adjustments_type",
                table: "financial_credit_card_invoice_adjustments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_invoice_adjustments_type_nature",
                table: "financial_credit_card_invoice_adjustments");

            migrationBuilder.DropIndex(
                name: "ix_financial_credit_card_installments_workspace_id_official_ca",
                table: "financial_credit_card_installments");

            migrationBuilder.DropColumn(
                name: "official_closed_at",
                table: "financial_credit_card_invoices");

            migrationBuilder.DropColumn(
                name: "official_total",
                table: "financial_credit_card_invoices");

            migrationBuilder.DropColumn(
                name: "official_version",
                table: "financial_credit_card_invoices");

            migrationBuilder.DropColumn(
                name: "official_category_id",
                table: "financial_credit_card_installments");

            migrationBuilder.AddCheckConstraint(
                name: "ck_invoice_adjustments_type",
                table: "financial_credit_card_invoice_adjustments",
                sql: "type IN ('Fee', 'Credit', 'Refund')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_invoice_adjustments_type_nature",
                table: "financial_credit_card_invoice_adjustments",
                sql: "(type = 'Fee' AND nature = 'Debit') OR (type IN ('Credit', 'Refund') AND nature = 'Credit')");
        }
    }
}
