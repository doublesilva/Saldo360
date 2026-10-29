using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCardSpendingTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "financial_card_spending_targets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payer_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    amount = table.Column<decimal>(type: "numeric", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_card_spending_targets", x => x.id);
                    table.CheckConstraint("ck_card_spending_target_amount", "amount IS NULL OR (amount >= 0 AND scale(amount) <= 2)");
                    table.CheckConstraint("ck_card_spending_target_competence", "EXTRACT(DAY FROM effective_from) = 1");
                    table.ForeignKey(
                        name: "fk_financial_card_spending_targets_financial_accounts_payer_ac",
                        column: x => x.payer_account_id,
                        principalTable: "financial_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_card_spending_targets_financial_workspaces_worksp",
                        column: x => x.workspace_id,
                        principalTable: "financial_workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_financial_card_spending_targets_payer_account_id",
                table: "financial_card_spending_targets",
                column: "payer_account_id");

            migrationBuilder.CreateIndex(
                name: "ux_card_spending_targets_account_from",
                table: "financial_card_spending_targets",
                columns: new[] { "workspace_id", "payer_account_id", "effective_from" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "financial_card_spending_targets");
        }
    }
}
