using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBudgetBootstrapExtensions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_imported_installments_number",
                table: "financial_credit_card_imported_installments");

            migrationBuilder.AlterColumn<Guid>(
                name: "financial_account_id",
                table: "financial_recurrences",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "financial_credit_card_id",
                table: "financial_recurrences",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "planned_transaction_id",
                table: "financial_recurrence_materializations",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "credit_card_purchase_id",
                table: "financial_recurrence_materializations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<DateOnly>(
                name: "transaction_date",
                table: "financial_credit_card_imported_installments",
                type: "date",
                nullable: true,
                oldClrType: typeof(DateOnly),
                oldType: "date");

            migrationBuilder.AlterColumn<int>(
                name: "row_number",
                table: "financial_credit_card_imported_installments",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<Guid>(
                name: "invoice_id",
                table: "financial_credit_card_imported_installments",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<int>(
                name: "installment_number",
                table: "financial_credit_card_imported_installments",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<int>(
                name: "installment_count",
                table: "financial_credit_card_imported_installments",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<Guid>(
                name: "import_id",
                table: "financial_credit_card_imported_installments",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<DateOnly>(
                name: "competence",
                table: "financial_credit_card_imported_installments",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "due_date",
                table: "financial_credit_card_imported_installments",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_reference",
                table: "financial_credit_card_imported_installments",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_system",
                table: "financial_credit_card_imported_installments",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "financial_bootstrap_provenance",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_system = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    source_document = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    external_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    entity_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    metadata_json = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_bootstrap_provenance", x => x.id);
                    table.ForeignKey(
                        name: "fk_financial_bootstrap_provenance_financial_workspaces_workspa",
                        column: x => x.workspace_id,
                        principalTable: "financial_workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "financial_monthly_aggregate_targets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    competence = table.Column<DateOnly>(type: "date", nullable: false),
                    target_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    amount = table.Column<decimal>(type: "numeric", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_monthly_aggregate_targets", x => x.id);
                    table.CheckConstraint("ck_monthly_aggregate_amount", "amount >= 0 AND scale(amount) <= 2");
                    table.CheckConstraint("ck_monthly_aggregate_competence", "EXTRACT(DAY FROM competence) = 1");
                    table.CheckConstraint("ck_monthly_aggregate_type", "target_type IN ('FixedExpenses','CreditCards','TotalExpenses','RecoveryAmount')");
                    table.ForeignKey(
                        name: "fk_financial_monthly_aggregate_targets_financial_workspaces_wo",
                        column: x => x.workspace_id,
                        principalTable: "financial_workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_financial_recurrences_workspace_id_financial_credit_card_id",
                table: "financial_recurrences",
                columns: new[] { "workspace_id", "financial_credit_card_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_recurrences_card_expense",
                table: "financial_recurrences",
                sql: "financial_credit_card_id IS NULL OR type = 'Expense'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_recurrences_destination",
                table: "financial_recurrences",
                sql: "(financial_account_id IS NOT NULL) <> (financial_credit_card_id IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_financial_recurrence_materializations_workspace_id_credit_c",
                table: "financial_recurrence_materializations",
                columns: new[] { "workspace_id", "credit_card_purchase_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_recurrence_materialization_destination",
                table: "financial_recurrence_materializations",
                sql: "(planned_transaction_id IS NOT NULL) <> (credit_card_purchase_id IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_imported_installments_competence",
                table: "financial_credit_card_imported_installments",
                sql: "competence IS NULL OR EXTRACT(DAY FROM competence) = 1");

            migrationBuilder.AddCheckConstraint(
                name: "ck_imported_installments_known",
                table: "financial_credit_card_imported_installments",
                sql: "invoice_id IS NOT NULL OR (is_historical AND competence IS NOT NULL AND source_system IS NOT NULL AND external_reference IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_imported_installments_legacy_fields",
                table: "financial_credit_card_imported_installments",
                sql: "source_system IS NOT NULL OR (transaction_date IS NOT NULL AND installment_number IS NOT NULL AND installment_count IS NOT NULL AND import_id IS NOT NULL AND row_number IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_imported_installments_number",
                table: "financial_credit_card_imported_installments",
                sql: "(installment_number IS NULL OR installment_number >= 1) AND (installment_count IS NULL OR installment_count >= 1) AND (installment_number IS NULL OR installment_count IS NULL OR installment_number <= installment_count)");

            migrationBuilder.CreateIndex(
                name: "ix_financial_bootstrap_provenance_workspace_id_source_system_s",
                table: "financial_bootstrap_provenance",
                columns: new[] { "workspace_id", "source_system", "source_document", "external_reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_financial_monthly_aggregate_targets_workspace_id_competence",
                table: "financial_monthly_aggregate_targets",
                columns: new[] { "workspace_id", "competence", "target_type" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_recurrence_materializations_financial_credit_card",
                table: "financial_recurrence_materializations",
                columns: new[] { "workspace_id", "credit_card_purchase_id" },
                principalTable: "financial_credit_card_purchases",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_recurrences_financial_credit_cards_workspace_id_f",
                table: "financial_recurrences",
                columns: new[] { "workspace_id", "financial_credit_card_id" },
                principalTable: "financial_credit_cards",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Restrict);

            // New financial tables must participate in the same reset/writer coordination as existing ones.
            migrationBuilder.Sql("""
                CREATE TRIGGER saldo360_financial_write_guard BEFORE INSERT OR UPDATE OR DELETE
                    ON financial_bootstrap_provenance FOR EACH ROW EXECUTE FUNCTION saldo360_financial_write_guard();
                CREATE TRIGGER saldo360_financial_write_guard BEFORE INSERT OR UPDATE OR DELETE
                    ON financial_monthly_aggregate_targets FOR EACH ROW EXECUTE FUNCTION saldo360_financial_write_guard();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $guard$
                BEGIN
                    IF EXISTS (SELECT 1 FROM financial_bootstrap_provenance)
                        OR EXISTS (SELECT 1 FROM financial_monthly_aggregate_targets)
                        OR EXISTS (SELECT 1 FROM financial_recurrences WHERE financial_credit_card_id IS NOT NULL)
                        OR EXISTS (SELECT 1 FROM financial_recurrence_materializations WHERE credit_card_purchase_id IS NOT NULL)
                        OR EXISTS (SELECT 1 FROM financial_credit_card_imported_installments
                            WHERE competence IS NOT NULL OR source_system IS NOT NULL OR invoice_id IS NULL) THEN
                        RAISE EXCEPTION 'BOOTSTRAP_DOWN_REQUIRES_RECOVERY_PLAN';
                    END IF;
                END $guard$;
                """);
            migrationBuilder.DropForeignKey(
                name: "fk_financial_recurrence_materializations_financial_credit_card",
                table: "financial_recurrence_materializations");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_recurrences_financial_credit_cards_workspace_id_f",
                table: "financial_recurrences");

            migrationBuilder.DropTable(
                name: "financial_bootstrap_provenance");

            migrationBuilder.DropTable(
                name: "financial_monthly_aggregate_targets");

            migrationBuilder.DropIndex(
                name: "ix_financial_recurrences_workspace_id_financial_credit_card_id",
                table: "financial_recurrences");

            migrationBuilder.DropCheckConstraint(
                name: "ck_recurrences_card_expense",
                table: "financial_recurrences");

            migrationBuilder.DropCheckConstraint(
                name: "ck_recurrences_destination",
                table: "financial_recurrences");

            migrationBuilder.DropIndex(
                name: "ix_financial_recurrence_materializations_workspace_id_credit_c",
                table: "financial_recurrence_materializations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_recurrence_materialization_destination",
                table: "financial_recurrence_materializations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_imported_installments_competence",
                table: "financial_credit_card_imported_installments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_imported_installments_known",
                table: "financial_credit_card_imported_installments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_imported_installments_legacy_fields",
                table: "financial_credit_card_imported_installments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_imported_installments_number",
                table: "financial_credit_card_imported_installments");

            migrationBuilder.DropColumn(
                name: "financial_credit_card_id",
                table: "financial_recurrences");

            migrationBuilder.DropColumn(
                name: "credit_card_purchase_id",
                table: "financial_recurrence_materializations");

            migrationBuilder.DropColumn(
                name: "competence",
                table: "financial_credit_card_imported_installments");

            migrationBuilder.DropColumn(
                name: "due_date",
                table: "financial_credit_card_imported_installments");

            migrationBuilder.DropColumn(
                name: "external_reference",
                table: "financial_credit_card_imported_installments");

            migrationBuilder.DropColumn(
                name: "source_system",
                table: "financial_credit_card_imported_installments");

            migrationBuilder.AlterColumn<Guid>(
                name: "financial_account_id",
                table: "financial_recurrences",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "planned_transaction_id",
                table: "financial_recurrence_materializations",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateOnly>(
                name: "transaction_date",
                table: "financial_credit_card_imported_installments",
                type: "date",
                nullable: false,
                oldClrType: typeof(DateOnly),
                oldType: "date",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "row_number",
                table: "financial_credit_card_imported_installments",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "invoice_id",
                table: "financial_credit_card_imported_installments",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "installment_number",
                table: "financial_credit_card_imported_installments",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "installment_count",
                table: "financial_credit_card_imported_installments",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "import_id",
                table: "financial_credit_card_imported_installments",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_imported_installments_number",
                table: "financial_credit_card_imported_installments",
                sql: "installment_number >= 1 AND installment_number <= installment_count");
        }
    }
}
