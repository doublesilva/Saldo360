using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBudgetRuleChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "financial_budget_rule_changes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    target = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    competence = table.Column<DateOnly>(type: "date", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    action = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    amount = table.Column<decimal>(type: "numeric", nullable: true),
                    note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_budget_rule_changes", x => x.id);
                    table.CheckConstraint("ck_budget_rule_amount", "(action = 'Set' AND amount IS NOT NULL AND amount >= 0 AND scale(amount) <= 2) OR (action <> 'Set' AND amount IS NULL)");
                    table.CheckConstraint("ck_budget_rule_category", "(target = 'Category') = (category_id IS NOT NULL)");
                    table.CheckConstraint("ck_budget_rule_competence", "EXTRACT(DAY FROM competence) = 1");
                    table.CheckConstraint("ck_budget_rule_restore", "action <> 'Restore' OR scope = 'OnlyCompetence'");
                    table.CheckConstraint("ck_budget_rule_revision", "revision >= 1");
                    table.ForeignKey(
                        name: "fk_financial_budget_rule_changes_financial_workspaces_workspac",
                        column: x => x.workspace_id,
                        principalTable: "financial_workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_financial_budget_rule_changes_workspace_id_competence",
                table: "financial_budget_rule_changes",
                columns: new[] { "workspace_id", "competence" });

            migrationBuilder.CreateIndex(
                name: "ux_budget_rule_changes_key_revision",
                table: "financial_budget_rule_changes",
                columns: new[] { "workspace_id", "scope", "target", "category_id", "competence", "revision" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "financial_budget_rule_changes");
        }
    }
}
