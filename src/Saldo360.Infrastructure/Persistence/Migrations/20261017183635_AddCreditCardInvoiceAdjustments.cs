using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCreditCardInvoiceAdjustments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "financial_credit_card_invoice_adjustments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    credit_card_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    nature = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    transaction_date = table.Column<DateOnly>(type: "date", nullable: false),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    amount = table.Column<decimal>(type: "numeric", nullable: false),
                    external_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_credit_card_invoice_adjustments", x => x.id);
                    table.CheckConstraint("ck_invoice_adjustments_amount_positive", "amount > 0");
                    table.CheckConstraint("ck_invoice_adjustments_amount_scale", "scale(amount) <= 2");
                    table.CheckConstraint("ck_invoice_adjustments_nature", "nature IN ('Debit', 'Credit')");
                    table.CheckConstraint("ck_invoice_adjustments_status", "status IN ('Active', 'Cancelled')");
                    table.CheckConstraint("ck_invoice_adjustments_type", "type IN ('Fee', 'Credit', 'Refund')");
                    table.CheckConstraint("ck_invoice_adjustments_type_nature", "(type = 'Fee' AND nature = 'Debit') OR (type IN ('Credit', 'Refund') AND nature = 'Credit')");
                    table.ForeignKey(
                        name: "fk_financial_credit_card_invoice_adjustments_application_users",
                        column: x => x.cancelled_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_credit_card_invoice_adjustments_application_users1",
                        column: x => x.created_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_credit_card_invoice_adjustments_families_family_id",
                        column: x => x.family_id,
                        principalTable: "families",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_credit_card_invoice_adjustments_financial_categor",
                        columns: x => new { x.family_id, x.category_id },
                        principalTable: "financial_categories",
                        principalColumns: new[] { "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_credit_card_invoice_adjustments_financial_credit_",
                        columns: x => new { x.family_id, x.credit_card_id },
                        principalTable: "financial_credit_cards",
                        principalColumns: new[] { "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_credit_card_invoice_adjustments_financial_credit_1",
                        columns: x => new { x.family_id, x.invoice_id },
                        principalTable: "financial_credit_card_invoices",
                        principalColumns: new[] { "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_card_invoice_adjustments_cancelled_by_user",
                table: "financial_credit_card_invoice_adjustments",
                column: "cancelled_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_card_invoice_adjustments_created_by_user_id",
                table: "financial_credit_card_invoice_adjustments",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_card_invoice_adjustments_family_id_categor",
                table: "financial_credit_card_invoice_adjustments",
                columns: new[] { "family_id", "category_id" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_card_invoice_adjustments_family_id_credit_",
                table: "financial_credit_card_invoice_adjustments",
                columns: new[] { "family_id", "credit_card_id", "external_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_card_invoice_adjustments_family_id_invoice",
                table: "financial_credit_card_invoice_adjustments",
                columns: new[] { "family_id", "invoice_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "financial_credit_card_invoice_adjustments");
        }
    }
}
