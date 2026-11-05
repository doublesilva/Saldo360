using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFoodProvision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "financial_food_provisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    competence = table.Column<DateOnly>(type: "date", nullable: true),
                    monthly_amount = table.Column<decimal>(type: "numeric", nullable: false),
                    category_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_food_provisions", x => x.id);
                    table.CheckConstraint("ck_food_provision_amount", "monthly_amount >= 0 AND scale(monthly_amount) <= 2");
                    table.CheckConstraint("ck_food_provision_competence", "competence IS NULL OR EXTRACT(DAY FROM competence) = 1");
                    table.ForeignKey(
                        name: "fk_financial_food_provisions_financial_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "financial_workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ux_food_provisions_workspace_competence",
                table: "financial_food_provisions",
                columns: new[] { "workspace_id", "competence" },
                unique: true,
                filter: "competence IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_food_provisions_workspace_default",
                table: "financial_food_provisions",
                column: "workspace_id",
                unique: true,
                filter: "competence IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "financial_food_provisions");
        }
    }
}
