using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCategoryHierarchyAndExpenseNature : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_financial_categories_workspace_id_type_name",
                table: "financial_categories");

            migrationBuilder.AddColumn<string>(
                name: "expense_nature",
                table: "financial_transactions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Undefined");

            migrationBuilder.AddColumn<string>(
                name: "expense_nature",
                table: "financial_recurrences",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Undefined");

            migrationBuilder.AddColumn<string>(
                name: "expense_nature",
                table: "financial_planned_transactions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Undefined");

            migrationBuilder.AddColumn<string>(
                name: "expense_nature",
                table: "financial_debt_installments",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Undefined");

            migrationBuilder.AddColumn<string>(
                name: "expense_nature",
                table: "financial_credit_card_purchases",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Undefined");

            migrationBuilder.AddColumn<Guid>(
                name: "parent_category_id",
                table: "financial_categories",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_financial_transactions_expense_nature",
                table: "financial_transactions",
                sql: "expense_nature IN ('Undefined', 'Fixed', 'Variable') AND (type = 'Expense' OR expense_nature = 'Undefined')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_recurrences_expense_nature",
                table: "financial_recurrences",
                sql: "expense_nature IN ('Undefined', 'Fixed', 'Variable') AND (type = 'Expense' OR expense_nature = 'Undefined')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_financial_planned_transactions_expense_nature",
                table: "financial_planned_transactions",
                sql: "expense_nature IN ('Undefined', 'Fixed', 'Variable') AND (type = 'Expense' OR expense_nature = 'Undefined')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_debt_installment_expense_nature",
                table: "financial_debt_installments",
                sql: "expense_nature IN ('Undefined', 'Fixed', 'Variable')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_credit_card_purchases_expense_nature",
                table: "financial_credit_card_purchases",
                sql: "expense_nature IN ('Undefined', 'Fixed', 'Variable')");

            migrationBuilder.CreateIndex(
                name: "ix_financial_categories_workspace_id_parent_category_id_type",
                table: "financial_categories",
                columns: new[] { "workspace_id", "parent_category_id", "type" });

            migrationBuilder.CreateIndex(
                name: "ux_financial_categories_parent_name",
                table: "financial_categories",
                columns: new[] { "parent_category_id", "name" },
                unique: true,
                filter: "parent_category_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_financial_categories_workspace_id_type_name",
                table: "financial_categories",
                columns: new[] { "workspace_id", "type", "name" },
                unique: true,
                filter: "parent_category_id IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_financial_categories_not_own_parent",
                table: "financial_categories",
                sql: "parent_category_id IS NULL OR parent_category_id <> id");

            migrationBuilder.AddForeignKey(
                name: "fk_financial_categories_parent_same_workspace_type",
                table: "financial_categories",
                columns: new[] { "workspace_id", "parent_category_id", "type" },
                principalTable: "financial_categories",
                principalColumns: new[] { "workspace_id", "id", "type" },
                onDelete: ReferentialAction.Restrict);

            // No máximo dois níveis: a principal de uma subcategoria não pode ser subcategoria, e quem tem subcategorias não pode virar subcategoria.
            // A FK composta acima já garante o mesmo orçamento e o mesmo tipo; o trigger cobre o que um índice ou uma FK não expressam.
            migrationBuilder.Sql("""
                CREATE FUNCTION financial_categories_enforce_two_levels() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.parent_category_id IS NOT NULL THEN
                        IF EXISTS (SELECT 1 FROM financial_categories p WHERE p.id = NEW.parent_category_id AND p.parent_category_id IS NOT NULL)
                           OR EXISTS (SELECT 1 FROM financial_categories c WHERE c.parent_category_id = NEW.id) THEN
                            RAISE EXCEPTION 'CATEGORY_MAX_TWO_LEVELS' USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER trg_financial_categories_two_levels BEFORE INSERT OR UPDATE OF parent_category_id ON financial_categories
                    FOR EACH ROW EXECUTE FUNCTION financial_categories_enforce_two_levels();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_financial_categories_two_levels ON financial_categories; DROP FUNCTION IF EXISTS financial_categories_enforce_two_levels();");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_categories_parent_same_workspace_type",
                table: "financial_categories");

            migrationBuilder.DropCheckConstraint(
                name: "ck_financial_transactions_expense_nature",
                table: "financial_transactions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_recurrences_expense_nature",
                table: "financial_recurrences");

            migrationBuilder.DropCheckConstraint(
                name: "ck_financial_planned_transactions_expense_nature",
                table: "financial_planned_transactions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_debt_installment_expense_nature",
                table: "financial_debt_installments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_credit_card_purchases_expense_nature",
                table: "financial_credit_card_purchases");

            migrationBuilder.DropIndex(
                name: "ix_financial_categories_workspace_id_parent_category_id_type",
                table: "financial_categories");

            migrationBuilder.DropIndex(
                name: "ux_financial_categories_parent_name",
                table: "financial_categories");

            migrationBuilder.DropIndex(
                name: "ux_financial_categories_workspace_id_type_name",
                table: "financial_categories");

            migrationBuilder.DropCheckConstraint(
                name: "ck_financial_categories_not_own_parent",
                table: "financial_categories");

            migrationBuilder.DropColumn(
                name: "expense_nature",
                table: "financial_transactions");

            migrationBuilder.DropColumn(
                name: "expense_nature",
                table: "financial_recurrences");

            migrationBuilder.DropColumn(
                name: "expense_nature",
                table: "financial_planned_transactions");

            migrationBuilder.DropColumn(
                name: "expense_nature",
                table: "financial_debt_installments");

            migrationBuilder.DropColumn(
                name: "expense_nature",
                table: "financial_credit_card_purchases");

            migrationBuilder.DropColumn(
                name: "parent_category_id",
                table: "financial_categories");

            migrationBuilder.CreateIndex(
                name: "ux_financial_categories_workspace_id_type_name",
                table: "financial_categories",
                columns: new[] { "workspace_id", "type", "name" },
                unique: true);
        }
    }
}
