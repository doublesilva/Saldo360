using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFinancialBudgets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "financial_budgets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    competence = table.Column<DateOnly>(type: "date", nullable: false),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    archived_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_budgets", x => x.id);
                    table.UniqueConstraint("ak_financial_budgets_family_id_id", x => new { x.family_id, x.id });
                    table.CheckConstraint("ck_financial_budgets_archived", "is_archived = (archived_at IS NOT NULL) AND (archived_at IS NULL) = (archived_by_user_id IS NULL)");
                    table.CheckConstraint("ck_financial_budgets_competence_first_day", "extract(day from competence) = 1");
                    table.CheckConstraint("ck_financial_budgets_description_trimmed", "description IS NULL OR octet_length(description) = octet_length(btrim(description))");
                    table.ForeignKey(
                        name: "fk_financial_budgets_application_users_archived_by_user_id",
                        column: x => x.archived_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_budgets_application_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_budgets_application_users_updated_by_user_id",
                        column: x => x.updated_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_budgets_families_family_id",
                        column: x => x.family_id,
                        principalTable: "families",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "financial_budget_targets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    budget_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    planned_amount = table.Column<decimal>(type: "numeric", nullable: false),
                    grouping_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    archived_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_budget_targets", x => x.id);
                    table.CheckConstraint("ck_financial_budget_targets_amount_nonnegative", "planned_amount >= 0");
                    table.CheckConstraint("ck_financial_budget_targets_amount_range", "planned_amount < 10000000000000");
                    table.CheckConstraint("ck_financial_budget_targets_amount_scale", "scale(planned_amount) <= 2");
                    table.CheckConstraint("ck_financial_budget_targets_archived", "is_archived = (archived_at IS NOT NULL) AND (archived_at IS NULL) = (archived_by_user_id IS NULL)");
                    table.CheckConstraint("ck_financial_budget_targets_category_type", "category_type = 'Expense'");
                    table.CheckConstraint("ck_financial_budget_targets_grouping_type", "grouping_type = 'Category'");
                    table.CheckConstraint("ck_financial_budget_targets_name_not_blank", "btrim(name) <> ''");
                    table.CheckConstraint("ck_financial_budget_targets_name_trimmed", "octet_length(name) = octet_length(btrim(name))");
                    table.ForeignKey(
                        name: "fk_financial_budget_targets_application_users_archived_by_user",
                        column: x => x.archived_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_budget_targets_application_users_created_by_user_",
                        column: x => x.created_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_budget_targets_application_users_updated_by_user_",
                        column: x => x.updated_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_budget_targets_budgets_same_family",
                        columns: x => new { x.family_id, x.budget_id },
                        principalTable: "financial_budgets",
                        principalColumns: new[] { "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_budget_targets_categories_same_family_expense",
                        columns: x => new { x.family_id, x.category_id, x.category_type },
                        principalTable: "financial_categories",
                        principalColumns: new[] { "family_id", "id", "type" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_financial_budget_targets_archived_by_user_id",
                table: "financial_budget_targets",
                column: "archived_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_financial_budget_targets_created_by_user_id",
                table: "financial_budget_targets",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_financial_budget_targets_family_id_budget_id",
                table: "financial_budget_targets",
                columns: new[] { "family_id", "budget_id" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_budget_targets_family_id_category_id_category_type",
                table: "financial_budget_targets",
                columns: new[] { "family_id", "category_id", "category_type" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_budget_targets_updated_by_user_id",
                table: "financial_budget_targets",
                column: "updated_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_financial_budget_targets_budget_grouping_category",
                table: "financial_budget_targets",
                columns: new[] { "budget_id", "grouping_type", "category_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_financial_budgets_archived_by_user_id",
                table: "financial_budgets",
                column: "archived_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_financial_budgets_created_by_user_id",
                table: "financial_budgets",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_financial_budgets_updated_by_user_id",
                table: "financial_budgets",
                column: "updated_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ux_financial_budgets_family_id_competence",
                table: "financial_budgets",
                columns: new[] { "family_id", "competence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "financial_budget_targets");

            migrationBuilder.DropTable(
                name: "financial_budgets");
        }
    }
}
