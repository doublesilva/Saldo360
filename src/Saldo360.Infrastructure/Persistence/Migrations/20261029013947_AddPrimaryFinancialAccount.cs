using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPrimaryFinancialAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_primary",
                table: "financial_accounts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "ux_financial_accounts_primary",
                table: "financial_accounts",
                column: "workspace_id",
                unique: true,
                filter: "is_primary");

            migrationBuilder.AddCheckConstraint(
                name: "ck_financial_accounts_primary_eligible",
                table: "financial_accounts",
                sql: "NOT is_primary OR (is_active AND type IN ('Checking', 'Savings'))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_financial_accounts_primary",
                table: "financial_accounts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_financial_accounts_primary_eligible",
                table: "financial_accounts");

            migrationBuilder.DropColumn(
                name: "is_primary",
                table: "financial_accounts");
        }
    }
}
