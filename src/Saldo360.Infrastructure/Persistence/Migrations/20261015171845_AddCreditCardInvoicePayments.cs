using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCreditCardInvoicePayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "category_id",
                table: "financial_transactions",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "origin",
                table: "financial_transactions",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "Manual");

            migrationBuilder.AddUniqueConstraint(
                name: "ak_financial_transactions_family_id_id",
                table: "financial_transactions",
                columns: new[] { "family_id", "id" });

            migrationBuilder.CreateTable(
                name: "financial_credit_card_invoice_payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    financial_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_date = table.Column<DateOnly>(type: "date", nullable: false),
                    amount = table.Column<decimal>(type: "numeric", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_credit_card_invoice_payments", x => x.id);
                    table.CheckConstraint("ck_credit_card_invoice_payments_amount_positive", "amount > 0");
                    table.CheckConstraint("ck_credit_card_invoice_payments_amount_scale", "scale(amount) <= 2");
                    table.ForeignKey(
                        name: "fk_financial_credit_card_invoice_payments_families_family_id",
                        column: x => x.family_id,
                        principalTable: "families",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_credit_card_invoice_payments_financial_credit_car",
                        columns: x => new { x.family_id, x.invoice_id },
                        principalTable: "financial_credit_card_invoices",
                        principalColumns: new[] { "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_credit_card_invoice_payments_financial_transactio",
                        columns: x => new { x.family_id, x.transaction_id },
                        principalTable: "financial_transactions",
                        principalColumns: new[] { "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_card_invoice_payments_family_id_invoice_id",
                table: "financial_credit_card_invoice_payments",
                columns: new[] { "family_id", "invoice_id", "payment_date" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_card_invoice_payments_family_id_transactio",
                table: "financial_credit_card_invoice_payments",
                columns: new[] { "family_id", "transaction_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "financial_credit_card_invoice_payments");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_financial_transactions_family_id_id",
                table: "financial_transactions");

            migrationBuilder.DropColumn(
                name: "origin",
                table: "financial_transactions");

            migrationBuilder.AlterColumn<Guid>(
                name: "category_id",
                table: "financial_transactions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
