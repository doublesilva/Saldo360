using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRecoveryAccountToPlanningScenario : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "recovery_account_id",
                table: "financial_planning_scenarios",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_financial_planning_scenarios_family_id_recovery_account_id",
                table: "financial_planning_scenarios",
                columns: new[] { "family_id", "recovery_account_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_financial_planning_scenarios_financial_accounts_family_id_r",
                table: "financial_planning_scenarios",
                columns: new[] { "family_id", "recovery_account_id" },
                principalTable: "financial_accounts",
                principalColumns: new[] { "family_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_financial_planning_scenarios_financial_accounts_family_id_r",
                table: "financial_planning_scenarios");

            migrationBuilder.DropIndex(
                name: "ix_financial_planning_scenarios_family_id_recovery_account_id",
                table: "financial_planning_scenarios");

            migrationBuilder.DropColumn(
                name: "recovery_account_id",
                table: "financial_planning_scenarios");
        }
    }
}
