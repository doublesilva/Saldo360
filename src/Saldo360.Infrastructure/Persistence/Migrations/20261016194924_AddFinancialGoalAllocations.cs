using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFinancialGoalAllocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "financial_goal_allocations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    goal_id = table.Column<Guid>(type: "uuid", nullable: false),
                    financial_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_type = table.Column<int>(type: "integer", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    effective_date = table.Column<DateOnly>(type: "date", nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_goal_allocations", x => x.id);
                    table.UniqueConstraint("ak_financial_goal_allocations_family_id_id", x => new { x.family_id, x.id });
                    table.CheckConstraint("ck_goal_allocations_amount", "amount > 0");
                    table.ForeignKey(
                        name: "fk_financial_goal_allocations_families_family_id",
                        column: x => x.family_id,
                        principalTable: "families",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_goal_allocations_financial_accounts_family_id_fin",
                        columns: x => new { x.family_id, x.financial_account_id },
                        principalTable: "financial_accounts",
                        principalColumns: new[] { "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_goal_allocations_financial_goals_family_id_goal_id",
                        columns: x => new { x.family_id, x.goal_id },
                        principalTable: "financial_goals",
                        principalColumns: new[] { "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_financial_goal_allocations_family_id_financial_account_id_c",
                table: "financial_goal_allocations",
                columns: new[] { "family_id", "financial_account_id", "cancelled_at" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_goal_allocations_family_id_goal_id_effective_date",
                table: "financial_goal_allocations",
                columns: new[] { "family_id", "goal_id", "effective_date" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_goal_allocations_family_id_operation_id",
                table: "financial_goal_allocations",
                columns: new[] { "family_id", "operation_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "financial_goal_allocations");
        }
    }
}
