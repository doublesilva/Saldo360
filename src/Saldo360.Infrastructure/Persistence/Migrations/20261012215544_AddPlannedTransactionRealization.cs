using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlannedTransactionRealization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "planned_transaction_id",
                table: "financial_transactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_financial_planned_transactions_family_id_id",
                table: "financial_planned_transactions",
                columns: new[] { "family_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_transactions_family_id_planned_transaction_id",
                table: "financial_transactions",
                columns: new[] { "family_id", "planned_transaction_id" });

            migrationBuilder.CreateIndex(
                name: "ux_financial_transactions_active_realization",
                table: "financial_transactions",
                column: "planned_transaction_id",
                unique: true,
                filter: "planned_transaction_id IS NOT NULL AND NOT is_cancelled");

            migrationBuilder.AddForeignKey(
                name: "fk_financial_transactions_planned_same_family",
                table: "financial_transactions",
                columns: new[] { "family_id", "planned_transaction_id" },
                principalTable: "financial_planned_transactions",
                principalColumns: new[] { "family_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_financial_transactions_planned_same_family",
                table: "financial_transactions");

            migrationBuilder.DropIndex(
                name: "ix_financial_transactions_family_id_planned_transaction_id",
                table: "financial_transactions");

            migrationBuilder.DropIndex(
                name: "ux_financial_transactions_active_realization",
                table: "financial_transactions");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_financial_planned_transactions_family_id_id",
                table: "financial_planned_transactions");

            migrationBuilder.DropColumn(
                name: "planned_transaction_id",
                table: "financial_transactions");
        }
    }
}
