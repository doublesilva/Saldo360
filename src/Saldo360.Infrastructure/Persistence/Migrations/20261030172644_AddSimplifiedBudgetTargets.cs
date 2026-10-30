using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSimplifiedBudgetTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "financial_card_spending_targets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_financial_budget_targets_category_type",
                table: "financial_budget_targets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_financial_budget_targets_grouping_type",
                table: "financial_budget_targets");

            migrationBuilder.AlterColumn<string>(
                name: "category_type",
                table: "financial_budget_targets",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            migrationBuilder.AlterColumn<Guid>(
                name: "category_id",
                table: "financial_budget_targets",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.CreateIndex(
                name: "ux_financial_budget_targets_budget_aggregate",
                table: "financial_budget_targets",
                columns: new[] { "budget_id", "grouping_type" },
                unique: true,
                filter: "category_id IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_financial_budget_targets_category_scope",
                table: "financial_budget_targets",
                sql: "(grouping_type = 'Category') = (category_id IS NOT NULL) AND (category_id IS NULL) = (category_type IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_financial_budget_targets_category_type",
                table: "financial_budget_targets",
                sql: "category_type IS NULL OR category_type = 'Expense'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_financial_budget_targets_grouping_type",
                table: "financial_budget_targets",
                sql: "grouping_type IN ('Category', 'FixedExpenses', 'AllCards')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Metas de despesas fixas e de todos os cartões não têm onde viver no esquema anterior (categoria obrigatória): recusa em vez de descartar.
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM financial_budget_targets WHERE grouping_type <> 'Category') THEN
                        RAISE EXCEPTION 'Down recusado: existem metas de despesas fixas ou de todos os cartões.';
                    END IF;
                END $$;
                """);

            migrationBuilder.DropIndex(
                name: "ux_financial_budget_targets_budget_aggregate",
                table: "financial_budget_targets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_financial_budget_targets_category_scope",
                table: "financial_budget_targets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_financial_budget_targets_category_type",
                table: "financial_budget_targets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_financial_budget_targets_grouping_type",
                table: "financial_budget_targets");

            migrationBuilder.AlterColumn<string>(
                name: "category_type",
                table: "financial_budget_targets",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20,
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "category_id",
                table: "financial_budget_targets",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "financial_card_spending_targets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    effective_from = table.Column<DateOnly>(type: "date", nullable: false),
                    payer_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false)
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

            migrationBuilder.AddCheckConstraint(
                name: "ck_financial_budget_targets_category_type",
                table: "financial_budget_targets",
                sql: "category_type = 'Expense'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_financial_budget_targets_grouping_type",
                table: "financial_budget_targets",
                sql: "grouping_type = 'Category'");

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
    }
}
