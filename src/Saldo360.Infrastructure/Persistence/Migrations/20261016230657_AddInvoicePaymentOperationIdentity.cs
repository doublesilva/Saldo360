using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoicePaymentOperationIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "operation_id",
                table: "financial_credit_card_invoice_payments",
                type: "uuid",
                nullable: true);

            // Pagamentos históricos recebem uma identidade técnica única. Ela serve apenas
            // para preservar o histórico; novos comandos sempre informam o operationId.
            migrationBuilder.Sql("UPDATE financial_credit_card_invoice_payments SET operation_id = gen_random_uuid() WHERE operation_id IS NULL");

            migrationBuilder.AlterColumn<Guid>(
                name: "operation_id",
                table: "financial_credit_card_invoice_payments",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_card_invoice_payments_family_id_operation_",
                table: "financial_credit_card_invoice_payments",
                columns: new[] { "family_id", "operation_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_financial_credit_card_invoice_payments_family_id_operation_",
                table: "financial_credit_card_invoice_payments");

            migrationBuilder.DropColumn(
                name: "operation_id",
                table: "financial_credit_card_invoice_payments");
        }
    }
}
