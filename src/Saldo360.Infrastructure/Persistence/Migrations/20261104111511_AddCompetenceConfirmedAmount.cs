using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCompetenceConfirmedAmount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "amount_confirmed_at",
                table: "financial_planned_transactions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "amount_confirmed_by_user_id",
                table: "financial_planned_transactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "confirmed_amount",
                table: "financial_planned_transactions",
                type: "numeric",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_financial_planned_transactions_amount_confirmed_by_user_id",
                table: "financial_planned_transactions",
                column: "amount_confirmed_by_user_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_financial_planned_transactions_confirmed_amount_valid",
                table: "financial_planned_transactions",
                sql: "confirmed_amount IS NULL OR (confirmed_amount > 0 AND scale(confirmed_amount) <= 2 AND confirmed_amount < 10000000000000)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_financial_planned_transactions_confirmed_triple",
                table: "financial_planned_transactions",
                sql: "(confirmed_amount IS NULL AND amount_confirmed_at IS NULL AND amount_confirmed_by_user_id IS NULL) OR (confirmed_amount IS NOT NULL AND amount_confirmed_at IS NOT NULL AND amount_confirmed_by_user_id IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "fk_financial_planned_transactions_application_users_amount_con",
                table: "financial_planned_transactions",
                column: "amount_confirmed_by_user_id",
                principalTable: "application_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_financial_planned_transactions_application_users_amount_con",
                table: "financial_planned_transactions");

            migrationBuilder.DropIndex(
                name: "ix_financial_planned_transactions_amount_confirmed_by_user_id",
                table: "financial_planned_transactions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_financial_planned_transactions_confirmed_amount_valid",
                table: "financial_planned_transactions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_financial_planned_transactions_confirmed_triple",
                table: "financial_planned_transactions");

            migrationBuilder.DropColumn(
                name: "amount_confirmed_at",
                table: "financial_planned_transactions");

            migrationBuilder.DropColumn(
                name: "amount_confirmed_by_user_id",
                table: "financial_planned_transactions");

            migrationBuilder.DropColumn(
                name: "confirmed_amount",
                table: "financial_planned_transactions");
        }
    }
}
