using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCardOperationKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "operation_kind",
                table: "financial_credit_card_purchases",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "Normal");

            migrationBuilder.AddCheckConstraint(
                name: "ck_credit_card_purchases_operation_kind",
                table: "financial_credit_card_purchases",
                sql: "operation_kind IN ('Normal', 'InvoiceFinancing')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_credit_card_purchases_operation_kind",
                table: "financial_credit_card_purchases");

            migrationBuilder.DropColumn(
                name: "operation_kind",
                table: "financial_credit_card_purchases");
        }
    }
}
