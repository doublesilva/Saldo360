using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCreditCardLastFourDigits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "last_four_digits",
                table: "financial_credit_cards",
                type: "character varying(4)",
                maxLength: 4,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_financial_credit_cards_last_four_digits",
                table: "financial_credit_cards",
                sql: "last_four_digits IS NULL OR last_four_digits ~ '^[0-9]{4}$'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_financial_credit_cards_last_four_digits",
                table: "financial_credit_cards");

            migrationBuilder.DropColumn(
                name: "last_four_digits",
                table: "financial_credit_cards");
        }
    }
}
