using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCreditCardInvoices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "invoice_id",
                table: "financial_credit_card_installments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "financial_credit_card_invoices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    credit_card_id = table.Column<Guid>(type: "uuid", nullable: false),
                    competence = table.Column<string>(type: "character(7)", fixedLength: true, maxLength: 7, nullable: false),
                    cycle_start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    closing_date = table.Column<DateOnly>(type: "date", nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_credit_card_invoices", x => x.id);
                    table.UniqueConstraint("ak_financial_credit_card_invoices_family_id_id", x => new { x.family_id, x.id });
                    table.ForeignKey(
                        name: "fk_financial_credit_card_invoices_financial_credit_cards_famil",
                        columns: x => new { x.family_id, x.credit_card_id },
                        principalTable: "financial_credit_cards",
                        principalColumns: new[] { "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_card_installments_family_id_invoice_id",
                table: "financial_credit_card_installments",
                columns: new[] { "family_id", "invoice_id" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_card_invoices_family_id_credit_card_id_clo",
                table: "financial_credit_card_invoices",
                columns: new[] { "family_id", "credit_card_id", "closing_date" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_credit_card_installments_invoices_same_family",
                table: "financial_credit_card_installments",
                columns: new[] { "family_id", "invoice_id" },
                principalTable: "financial_credit_card_invoices",
                principalColumns: new[] { "family_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_credit_card_installments_invoices_same_family",
                table: "financial_credit_card_installments");

            migrationBuilder.DropTable(
                name: "financial_credit_card_invoices");

            migrationBuilder.DropIndex(
                name: "ix_financial_credit_card_installments_family_id_invoice_id",
                table: "financial_credit_card_installments");

            migrationBuilder.DropColumn(
                name: "invoice_id",
                table: "financial_credit_card_installments");
        }
    }
}
