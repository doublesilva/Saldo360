using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOpeningBalanceToFinancialAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "opening_balance",
                table: "financial_accounts",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "opening_balance_date",
                table: "financial_accounts",
                type: "date",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_financial_accounts_opening_balance_pair",
                table: "financial_accounts",
                sql: "(opening_balance IS NULL) = (opening_balance_date IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_financial_accounts_opening_balance_range",
                table: "financial_accounts",
                sql: "abs(opening_balance) < 10000000000000");

            migrationBuilder.AddCheckConstraint(
                name: "ck_financial_accounts_opening_balance_scale",
                table: "financial_accounts",
                sql: "scale(opening_balance) <= 2");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_financial_accounts_opening_balance_pair",
                table: "financial_accounts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_financial_accounts_opening_balance_range",
                table: "financial_accounts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_financial_accounts_opening_balance_scale",
                table: "financial_accounts");

            migrationBuilder.DropColumn(
                name: "opening_balance",
                table: "financial_accounts");

            migrationBuilder.DropColumn(
                name: "opening_balance_date",
                table: "financial_accounts");
        }
    }
}
