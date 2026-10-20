using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IntroduceFinancialWorkspaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Explicit offline plan only. Empty test databases need no plan; populated bases fail closed.
            migrationBuilder.Sql("""
                CREATE TEMP TABLE financial_workspace_cutover_plan (
                    family_id uuid PRIMARY KEY, workspace_id uuid NOT NULL UNIQUE, owner_user_id uuid NOT NULL UNIQUE,
                    CHECK (family_id <> workspace_id),
                    CHECK (workspace_id <> '00000000-0000-0000-0000-000000000000'::uuid)
                ) ON COMMIT DROP;
                INSERT INTO financial_workspace_cutover_plan
                SELECT family_id, workspace_id, owner_user_id FROM jsonb_to_recordset(
                    COALESCE(NULLIF(current_setting('saldo360.workspace_cutover_plan', true), ''), '[]')::jsonb
                ) AS p(family_id uuid, workspace_id uuid, owner_user_id uuid);
                DO $guard$
                DECLARE t text; missing boolean;
                BEGIN
                    IF EXISTS (SELECT 1 FROM financial_workspace_cutover_plan p JOIN families f ON f.id = p.workspace_id) THEN
                        RAISE EXCEPTION 'WORKSPACE_ID_MUST_BE_INDEPENDENT: destination cannot reuse a family identity';
                    END IF;
                    FOR t IN SELECT table_name FROM information_schema.columns
                        WHERE table_schema = 'public' AND column_name = 'family_id'
                          AND (table_name LIKE 'financial_%' OR table_name = 'service_account_bindings')
                    LOOP
                        EXECUTE format('SELECT EXISTS (SELECT 1 FROM %I x WHERE NOT EXISTS (SELECT 1 FROM financial_workspace_cutover_plan p WHERE p.family_id = x.family_id))', t) INTO missing;
                        IF missing THEN RAISE EXCEPTION 'WORKSPACE_CUTOVER_PLAN_REQUIRED: populated financial data requires an explicitly approved offline ownership plan'; END IF;
                    END LOOP;
                END $guard$;
                """);
            migrationBuilder.DropForeignKey(
                name: "fk_financial_accounts_families_family_id",
                table: "financial_accounts");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_accounts_family_members_responsible",
                table: "financial_accounts");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_budgets_families_family_id",
                table: "financial_budgets");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_categories_families_family_id",
                table: "financial_categories");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_credit_card_import_batches_families_family_id",
                table: "financial_credit_card_import_batches");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_credit_card_import_items_financial_credit_cards_f",
                table: "financial_credit_card_import_items");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_credit_card_imported_installments_families_family",
                table: "financial_credit_card_imported_installments");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_credit_card_invoice_adjustments_families_family_id",
                table: "financial_credit_card_invoice_adjustments");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_credit_card_invoice_payments_families_family_id",
                table: "financial_credit_card_invoice_payments");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_credit_card_invoices_financial_credit_cards_famil",
                table: "financial_credit_card_invoices");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_credit_card_purchases_families_family_id",
                table: "financial_credit_card_purchases");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_credit_cards_families_family_id",
                table: "financial_credit_cards");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_credit_cards_family_members_holder",
                table: "financial_credit_cards");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_debt_installment_reconciliations_families_family_",
                table: "financial_debt_installment_reconciliations");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_debt_installments_families_family_id",
                table: "financial_debt_installments");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_debt_installments_financial_debts_family_id_debt_",
                table: "financial_debt_installments");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_debt_payments_families_family_id",
                table: "financial_debt_payments");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_debt_payments_financial_accounts_family_id_financ",
                table: "financial_debt_payments");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_debt_payments_financial_debt_installments_family_",
                table: "financial_debt_payments");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_debt_payments_financial_transactions_family_id_ba",
                table: "financial_debt_payments");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_debts_families_family_id",
                table: "financial_debts");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_debts_financial_accounts_family_id_responsible_ac",
                table: "financial_debts");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_document_inbox_items_families_family_id",
                table: "financial_document_inbox_items");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_document_inbox_items_financial_credit_cards_famil",
                table: "financial_document_inbox_items");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_document_inbox_items_financial_recurrences_family",
                table: "financial_document_inbox_items");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_email_collector_checkpoints_families_family_id",
                table: "financial_email_collector_checkpoints");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_goal_allocations_families_family_id",
                table: "financial_goal_allocations");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_goal_allocations_financial_accounts_family_id_fin",
                table: "financial_goal_allocations");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_goal_allocations_financial_goals_family_id_goal_id",
                table: "financial_goal_allocations");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_goals_families_family_id",
                table: "financial_goals");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_planned_invoice_reconciliations_families_family_id",
                table: "financial_planned_invoice_reconciliations");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_planned_transactions_families_family_id",
                table: "financial_planned_transactions");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_planning_scenarios_families_family_id",
                table: "financial_planning_scenarios");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_planning_scenarios_financial_accounts_family_id_r",
                table: "financial_planning_scenarios");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_recurrence_materializations_families_family_id",
                table: "financial_recurrence_materializations");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_recurrence_materializations_financial_planned_tra",
                table: "financial_recurrence_materializations");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_recurrences_families_family_id",
                table: "financial_recurrences");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_recurrences_financial_accounts_family_id_financia",
                table: "financial_recurrences");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_recurrences_financial_categories_family_id_financ",
                table: "financial_recurrences");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_transactions_families_family_id",
                table: "financial_transactions");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_transfers_families_family_id",
                table: "financial_transfers");

            migrationBuilder.DropForeignKey(
                name: "fk_service_account_bindings_families_family_id",
                table: "service_account_bindings");

            migrationBuilder.DropForeignKey(
                name: "fk_service_account_bindings_financial_recurrences_family_id_re",
                table: "service_account_bindings");

            migrationBuilder.Sql("ALTER TABLE financial_transactions RENAME CONSTRAINT ak_financial_transactions_family_id_id TO ak_financial_transactions_workspace_id_id;");

            migrationBuilder.Sql("ALTER TABLE financial_recurrences RENAME CONSTRAINT ak_financial_recurrences_family_id_id TO ak_financial_recurrences_workspace_id_id;");

            migrationBuilder.DropIndex(
                name: "ix_financial_recurrence_materializations_planned_transaction_id",
                table: "financial_recurrence_materializations");

            migrationBuilder.Sql("ALTER TABLE financial_planning_scenarios RENAME CONSTRAINT ak_financial_planning_scenarios_family_id_id TO ak_financial_planning_scenarios_workspace_id_id;");

            migrationBuilder.Sql("ALTER TABLE financial_planned_transactions RENAME CONSTRAINT ak_financial_planned_transactions_family_id_id TO ak_financial_planned_transactions_workspace_id_id;");

            migrationBuilder.Sql("ALTER TABLE financial_goals RENAME CONSTRAINT ak_financial_goals_family_id_id TO ak_financial_goals_workspace_id_id;");

            migrationBuilder.Sql("ALTER TABLE financial_goal_allocations RENAME CONSTRAINT ak_financial_goal_allocations_family_id_id TO ak_financial_goal_allocations_workspace_id_id;");

            migrationBuilder.Sql("ALTER TABLE financial_debts RENAME CONSTRAINT ak_financial_debts_family_id_id TO ak_financial_debts_workspace_id_id;");

            migrationBuilder.Sql("ALTER TABLE financial_debt_installments RENAME CONSTRAINT ak_financial_debt_installments_family_id_id TO ak_financial_debt_installments_workspace_id_id;");

            migrationBuilder.Sql("ALTER TABLE financial_credit_cards RENAME CONSTRAINT ak_financial_credit_cards_family_id_id TO ak_financial_credit_cards_workspace_id_id;");

            migrationBuilder.Sql("ALTER TABLE financial_credit_card_purchases RENAME CONSTRAINT ak_financial_credit_card_purchases_family_id_id TO ak_financial_credit_card_purchases_workspace_id_id;");

            migrationBuilder.Sql("ALTER TABLE financial_credit_card_invoices RENAME CONSTRAINT ak_financial_credit_card_invoices_family_id_id TO ak_financial_credit_card_invoices_workspace_id_id;");

            migrationBuilder.Sql("ALTER TABLE financial_credit_card_installments RENAME CONSTRAINT ak_financial_credit_card_installments_family_id_id TO ak_financial_credit_card_installments_workspace_id_id;");

            migrationBuilder.Sql("ALTER TABLE financial_credit_card_import_batches RENAME CONSTRAINT ak_financial_credit_card_import_batches_family_id_id TO ak_financial_credit_card_import_batches_workspace_id_id;");

            migrationBuilder.Sql("ALTER TABLE financial_categories RENAME CONSTRAINT ak_financial_categories_family_id_id TO ak_financial_categories_workspace_id_id;");

            migrationBuilder.Sql("ALTER TABLE financial_categories RENAME CONSTRAINT ak_financial_categories_family_id_id_type TO ak_financial_categories_workspace_id_id_type;");

            migrationBuilder.Sql("ALTER TABLE financial_budgets RENAME CONSTRAINT ak_financial_budgets_family_id_id TO ak_financial_budgets_workspace_id_id;");

            migrationBuilder.Sql("ALTER TABLE financial_accounts RENAME CONSTRAINT ak_financial_accounts_family_id_id TO ak_financial_accounts_workspace_id_id;");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_family_members_family_id_id",
                table: "family_members");

            migrationBuilder.RenameColumn(
                name: "family_id",
                table: "service_account_bindings",
                newName: "workspace_id");

            migrationBuilder.RenameIndex(
                name: "ix_service_account_bindings_family_id_recurring_template_id",
                table: "service_account_bindings",
                newName: "ix_service_account_bindings_workspace_id_recurring_template_id");

            migrationBuilder.RenameIndex(
                name: "ix_service_account_bindings_family_id_document_type_service_re",
                table: "service_account_bindings",
                newName: "ix_service_account_bindings_workspace_id_document_type_service");

            migrationBuilder.RenameIndex(
                name: "ix_service_account_bindings_family_id_document_type_account_re",
                table: "service_account_bindings",
                newName: "ix_service_account_bindings_workspace_id_document_type_account");

            migrationBuilder.RenameColumn(
                name: "family_id",
                table: "financial_transfers",
                newName: "workspace_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_transfers_family_id_transfer_date_id",
                table: "financial_transfers",
                newName: "ix_financial_transfers_workspace_id_transfer_date_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_transfers_family_id_source_account_id",
                table: "financial_transfers",
                newName: "ix_financial_transfers_workspace_id_source_account_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_transfers_family_id_destination_account_id",
                table: "financial_transfers",
                newName: "ix_financial_transfers_workspace_id_destination_account_id");

            migrationBuilder.RenameColumn(
                name: "family_id",
                table: "financial_transactions",
                newName: "workspace_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_transactions_family_id_transaction_date_id",
                table: "financial_transactions",
                newName: "ix_financial_transactions_workspace_id_transaction_date_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_transactions_family_id_planned_transaction_id",
                table: "financial_transactions",
                newName: "ix_financial_transactions_workspace_id_planned_transaction_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_transactions_family_id_category_id_type",
                table: "financial_transactions",
                newName: "ix_financial_transactions_workspace_id_category_id_type");

            migrationBuilder.RenameIndex(
                name: "ix_financial_transactions_family_id_account_id",
                table: "financial_transactions",
                newName: "ix_financial_transactions_workspace_id_account_id");

            migrationBuilder.RenameColumn(
                name: "family_id",
                table: "financial_recurrences",
                newName: "workspace_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_recurrences_family_id_status_start_date",
                table: "financial_recurrences",
                newName: "ix_financial_recurrences_workspace_id_status_start_date");

            migrationBuilder.RenameIndex(
                name: "ix_financial_recurrences_family_id_financial_category_id_type",
                table: "financial_recurrences",
                newName: "ix_financial_recurrences_workspace_id_financial_category_id_ty");

            migrationBuilder.RenameIndex(
                name: "ix_financial_recurrences_family_id_financial_account_id",
                table: "financial_recurrences",
                newName: "ix_financial_recurrences_workspace_id_financial_account_id");

            migrationBuilder.RenameColumn(
                name: "family_id",
                table: "financial_recurrence_materializations",
                newName: "workspace_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_recurrence_materializations_family_id_recurrence_1",
                table: "financial_recurrence_materializations",
                newName: "ix_financial_recurrence_materializations_workspace_id_recurren1");

            migrationBuilder.RenameIndex(
                name: "ix_financial_recurrence_materializations_family_id_recurrence_",
                table: "financial_recurrence_materializations",
                newName: "ix_financial_recurrence_materializations_workspace_id_recurren");

            migrationBuilder.RenameColumn(
                name: "family_id",
                table: "financial_planning_scenarios",
                newName: "workspace_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_planning_scenarios_family_id_status_start_month",
                table: "financial_planning_scenarios",
                newName: "ix_financial_planning_scenarios_workspace_id_status_start_month");

            migrationBuilder.RenameIndex(
                name: "ix_financial_planning_scenarios_family_id_recovery_account_id",
                table: "financial_planning_scenarios",
                newName: "ix_financial_planning_scenarios_workspace_id_recovery_account_");

            migrationBuilder.RenameColumn(
                name: "family_id",
                table: "financial_planning_monthly_parameters",
                newName: "workspace_id");

            migrationBuilder.RenameColumn(
                name: "family_id",
                table: "financial_planned_transactions",
                newName: "workspace_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_planned_transactions_family_id_due_date_id",
                table: "financial_planned_transactions",
                newName: "ix_financial_planned_transactions_workspace_id_due_date_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_planned_transactions_family_id_category_id_type",
                table: "financial_planned_transactions",
                newName: "ix_financial_planned_transactions_workspace_id_category_id_type");

            migrationBuilder.RenameIndex(
                name: "ix_financial_planned_transactions_family_id_account_id",
                table: "financial_planned_transactions",
                newName: "ix_financial_planned_transactions_workspace_id_account_id");

            migrationBuilder.RenameColumn(
                name: "family_id",
                table: "financial_planned_invoice_reconciliations",
                newName: "workspace_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_planned_invoice_reconciliations_family_id_planned",
                table: "financial_planned_invoice_reconciliations",
                newName: "ix_financial_planned_invoice_reconciliations_workspace_id_plan");

            migrationBuilder.RenameIndex(
                name: "ix_financial_planned_invoice_reconciliations_family_id_credit_",
                table: "financial_planned_invoice_reconciliations",
                newName: "ix_financial_planned_invoice_reconciliations_workspace_id_cred");

            migrationBuilder.RenameIndex(
                name: "ix_financial_planned_invoice_reconciliations_family_id_created",
                table: "financial_planned_invoice_reconciliations",
                newName: "ix_financial_planned_invoice_reconciliations_workspace_id_crea");

            migrationBuilder.RenameColumn(
                name: "family_id",
                table: "financial_goals",
                newName: "workspace_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_goals_family_id_status_target_date",
                table: "financial_goals",
                newName: "ix_financial_goals_workspace_id_status_target_date");

            migrationBuilder.RenameColumn(
                name: "family_id",
                table: "financial_goal_allocations",
                newName: "workspace_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_goal_allocations_family_id_operation_id",
                table: "financial_goal_allocations",
                newName: "ix_financial_goal_allocations_workspace_id_operation_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_goal_allocations_family_id_goal_id_effective_date",
                table: "financial_goal_allocations",
                newName: "ix_financial_goal_allocations_workspace_id_goal_id_effective_d");

            migrationBuilder.RenameIndex(
                name: "ix_financial_goal_allocations_family_id_financial_account_id_c",
                table: "financial_goal_allocations",
                newName: "ix_financial_goal_allocations_workspace_id_financial_account_i");

            migrationBuilder.RenameColumn(
                name: "family_id",
                table: "financial_email_collector_checkpoints",
                newName: "workspace_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_email_collector_checkpoints_family_id_profile_name",
                table: "financial_email_collector_checkpoints",
                newName: "ix_financial_email_collector_checkpoints_workspace_id_profile_");

            migrationBuilder.RenameColumn(
                name: "family_id",
                table: "financial_document_inbox_items",
                newName: "workspace_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_document_inbox_items_family_id_status_received_at",
                table: "financial_document_inbox_items",
                newName: "ix_financial_document_inbox_items_workspace_id_status_received");

            migrationBuilder.RenameIndex(
                name: "ix_financial_document_inbox_items_family_id_source_external_so",
                table: "financial_document_inbox_items",
                newName: "ix_financial_document_inbox_items_workspace_id_source_external");

            migrationBuilder.RenameIndex(
                name: "ix_financial_document_inbox_items_family_id_service_bill_recur",
                table: "financial_document_inbox_items",
                newName: "ix_financial_document_inbox_items_workspace_id_service_bill_re");

            migrationBuilder.RenameIndex(
                name: "ix_financial_document_inbox_items_family_id_service_bill_occur",
                table: "financial_document_inbox_items",
                newName: "ix_financial_document_inbox_items_workspace_id_service_bill_oc");

            migrationBuilder.RenameIndex(
                name: "ix_financial_document_inbox_items_family_id_credit_card_id",
                table: "financial_document_inbox_items",
                newName: "ix_financial_document_inbox_items_workspace_id_credit_card_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_document_inbox_items_family_id_content_hash",
                table: "financial_document_inbox_items",
                newName: "ix_financial_document_inbox_items_workspace_id_content_hash");

            migrationBuilder.RenameColumn(
                name: "family_id",
                table: "financial_debts",
                newName: "workspace_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_debts_family_id_status",
                table: "financial_debts",
                newName: "ix_financial_debts_workspace_id_status");

            migrationBuilder.RenameIndex(
                name: "ix_financial_debts_family_id_responsible_account_id",
                table: "financial_debts",
                newName: "ix_financial_debts_workspace_id_responsible_account_id");

            migrationBuilder.RenameColumn(
                name: "family_id",
                table: "financial_debt_payments",
                newName: "workspace_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_debt_payments_family_id_operation_id",
                table: "financial_debt_payments",
                newName: "ix_financial_debt_payments_workspace_id_operation_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_debt_payments_family_id_installment_id",
                table: "financial_debt_payments",
                newName: "ix_financial_debt_payments_workspace_id_installment_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_debt_payments_family_id_financial_account_id",
                table: "financial_debt_payments",
                newName: "ix_financial_debt_payments_workspace_id_financial_account_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_debt_payments_family_id_bank_transaction_id",
                table: "financial_debt_payments",
                newName: "ix_financial_debt_payments_workspace_id_bank_transaction_id");

            migrationBuilder.RenameColumn(
                name: "family_id",
                table: "financial_debt_installments",
                newName: "workspace_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_debt_installments_family_id_due_date",
                table: "financial_debt_installments",
                newName: "ix_financial_debt_installments_workspace_id_due_date");

            migrationBuilder.RenameIndex(
                name: "ix_financial_debt_installments_family_id_debt_id_installment_n",
                table: "financial_debt_installments",
                newName: "ix_financial_debt_installments_workspace_id_debt_id_installmen");

            migrationBuilder.RenameColumn(
                name: "family_id",
                table: "financial_debt_installment_reconciliations",
                newName: "workspace_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_debt_installment_reconciliations_family_id_planne",
                table: "financial_debt_installment_reconciliations",
                newName: "ix_financial_debt_installment_reconciliations_workspace_id_pla");

            migrationBuilder.RenameIndex(
                name: "ix_financial_debt_installment_reconciliations_family_id_debt_i",
                table: "financial_debt_installment_reconciliations",
                newName: "ix_financial_debt_installment_reconciliations_workspace_id_deb");

            migrationBuilder.RenameColumn(
                name: "family_id",
                table: "financial_credit_cards",
                newName: "workspace_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_cards_family_id_is_archived",
                table: "financial_credit_cards",
                newName: "ix_financial_credit_cards_workspace_id_is_archived");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_cards_family_id_holder_user_id",
                table: "financial_credit_cards",
                newName: "ix_financial_credit_cards_workspace_id_holder_user_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_cards_family_id_default_payment_account_id",
                table: "financial_credit_cards",
                newName: "ix_financial_credit_cards_workspace_id_default_payment_account");

            migrationBuilder.RenameColumn(
                name: "family_id",
                table: "financial_credit_card_purchases",
                newName: "workspace_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_card_purchases_family_id_credit_card_id_pu",
                table: "financial_credit_card_purchases",
                newName: "ix_financial_credit_card_purchases_workspace_id_credit_card_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_card_purchases_family_id_category_id",
                table: "financial_credit_card_purchases",
                newName: "ix_financial_credit_card_purchases_workspace_id_category_id");

            migrationBuilder.RenameColumn(
                name: "family_id",
                table: "financial_credit_card_invoices",
                newName: "workspace_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_card_invoices_family_id_credit_card_id_clo",
                table: "financial_credit_card_invoices",
                newName: "ix_financial_credit_card_invoices_workspace_id_credit_card_id_");

            migrationBuilder.RenameColumn(
                name: "family_id",
                table: "financial_credit_card_invoice_payments",
                newName: "workspace_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_card_invoice_payments_family_id_transactio",
                table: "financial_credit_card_invoice_payments",
                newName: "ix_financial_credit_card_invoice_payments_workspace_id_transac");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_card_invoice_payments_family_id_operation_",
                table: "financial_credit_card_invoice_payments",
                newName: "ix_financial_credit_card_invoice_payments_workspace_id_operati");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_card_invoice_payments_family_id_invoice_id",
                table: "financial_credit_card_invoice_payments",
                newName: "ix_financial_credit_card_invoice_payments_workspace_id_invoice");

            migrationBuilder.RenameColumn(
                name: "family_id",
                table: "financial_credit_card_invoice_adjustments",
                newName: "workspace_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_card_invoice_adjustments_family_id_invoice",
                table: "financial_credit_card_invoice_adjustments",
                newName: "ix_financial_credit_card_invoice_adjustments_workspace_id_invo");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_card_invoice_adjustments_family_id_credit_",
                table: "financial_credit_card_invoice_adjustments",
                newName: "ix_financial_credit_card_invoice_adjustments_workspace_id_cred");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_card_invoice_adjustments_family_id_categor",
                table: "financial_credit_card_invoice_adjustments",
                newName: "ix_financial_credit_card_invoice_adjustments_workspace_id_cate");

            migrationBuilder.RenameColumn(
                name: "family_id",
                table: "financial_credit_card_installments",
                newName: "workspace_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_card_installments_family_id_purchase_id_in",
                table: "financial_credit_card_installments",
                newName: "ix_financial_credit_card_installments_workspace_id_purchase_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_card_installments_family_id_invoice_id",
                table: "financial_credit_card_installments",
                newName: "ix_financial_credit_card_installments_workspace_id_invoice_id");

            migrationBuilder.RenameColumn(
                name: "family_id",
                table: "financial_credit_card_imported_installments",
                newName: "workspace_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_card_imported_installments_family_id_linke",
                table: "financial_credit_card_imported_installments",
                newName: "ix_financial_credit_card_imported_installments_workspace_id_li");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_card_imported_installments_family_id_invoi",
                table: "financial_credit_card_imported_installments",
                newName: "ix_financial_credit_card_imported_installments_workspace_id_in");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_card_imported_installments_family_id_credi1",
                table: "financial_credit_card_imported_installments",
                newName: "ix_financial_credit_card_imported_installments_workspace_id_cr1");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_card_imported_installments_family_id_credi",
                table: "financial_credit_card_imported_installments",
                newName: "ix_financial_credit_card_imported_installments_workspace_id_cr");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_card_imported_installments_family_id_categ",
                table: "financial_credit_card_imported_installments",
                newName: "ix_financial_credit_card_imported_installments_workspace_id_ca");

            migrationBuilder.RenameColumn(
                name: "family_id",
                table: "financial_credit_card_import_items",
                newName: "workspace_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_card_import_items_family_id_purchase_id",
                table: "financial_credit_card_import_items",
                newName: "ix_financial_credit_card_import_items_workspace_id_purchase_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_card_import_items_family_id_import_batch_id",
                table: "financial_credit_card_import_items",
                newName: "ix_financial_credit_card_import_items_workspace_id_import_batc");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_card_import_items_family_id_credit_card_id",
                table: "financial_credit_card_import_items",
                newName: "ix_financial_credit_card_import_items_workspace_id_credit_card");

            migrationBuilder.RenameColumn(
                name: "family_id",
                table: "financial_credit_card_import_batches",
                newName: "workspace_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_card_import_batches_family_id_credit_card_",
                table: "financial_credit_card_import_batches",
                newName: "ix_financial_credit_card_import_batches_workspace_id_credit_ca");

            migrationBuilder.RenameColumn(
                name: "family_id",
                table: "financial_categories",
                newName: "workspace_id");

            migrationBuilder.RenameIndex(
                name: "ux_financial_categories_family_id_type_name",
                table: "financial_categories",
                newName: "ux_financial_categories_workspace_id_type_name");

            migrationBuilder.RenameColumn(
                name: "family_id",
                table: "financial_budgets",
                newName: "workspace_id");

            migrationBuilder.RenameIndex(
                name: "ux_financial_budgets_family_id_competence",
                table: "financial_budgets",
                newName: "ux_financial_budgets_workspace_id_competence");

            migrationBuilder.RenameColumn(
                name: "family_id",
                table: "financial_budget_targets",
                newName: "workspace_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_budget_targets_family_id_category_id_category_type",
                table: "financial_budget_targets",
                newName: "ix_financial_budget_targets_workspace_id_category_id_category_");

            migrationBuilder.RenameIndex(
                name: "ix_financial_budget_targets_family_id_budget_id",
                table: "financial_budget_targets",
                newName: "ix_financial_budget_targets_workspace_id_budget_id");

            migrationBuilder.RenameColumn(
                name: "responsible_member_id",
                table: "financial_accounts",
                newName: "responsible_user_id");

            migrationBuilder.RenameColumn(
                name: "family_id",
                table: "financial_accounts",
                newName: "workspace_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_accounts_family_id_responsible_member_id",
                table: "financial_accounts",
                newName: "ix_financial_accounts_workspace_id_responsible_user_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_accounts_family_id_is_active",
                table: "financial_accounts",
                newName: "ix_financial_accounts_workspace_id_is_active");



































            migrationBuilder.CreateTable(
                name: "financial_workspaces",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_workspaces", x => x.id);
                    table.UniqueConstraint("ak_financial_workspaces_id_owner_user_id", x => new { x.id, x.owner_user_id });
                    table.CheckConstraint("ck_workspace_type", "type IN ('Personal', 'Shared')");
                    table.ForeignKey(
                        name: "fk_financial_workspaces_application_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_financial_recurrence_materializations_workspace_id_planned_",
                table: "financial_recurrence_materializations",
                columns: new[] { "workspace_id", "planned_transaction_id" });

            migrationBuilder.Sql("""
                -- Scope references must move together. Capture and restore the exact remaining
                -- financial FKs within this migration transaction; never use CASCADE.
                CREATE TEMP TABLE workspace_cutover_fks ON COMMIT DROP AS
                SELECT c.conrelid::regclass::text AS relation, c.conname AS name,
                       pg_get_constraintdef(c.oid) AS definition
                FROM pg_constraint c
                JOIN pg_class r ON r.oid = c.conrelid
                JOIN pg_namespace n ON n.oid = r.relnamespace
                WHERE c.contype = 'f' AND n.nspname = 'public'
                  AND (r.relname LIKE 'financial_%' OR r.relname = 'service_account_bindings');
                DO $detach$
                DECLARE c record;
                BEGIN
                    FOR c IN SELECT * FROM workspace_cutover_fks LOOP
                        EXECUTE format('ALTER TABLE %s DROP CONSTRAINT %I', c.relation, c.name);
                    END LOOP;
                END $detach$;
                INSERT INTO financial_workspaces (id, owner_user_id, name, type, is_active, created_at, updated_at)
                SELECT DISTINCT workspace_id, owner_user_id, 'Meu orçamento', 'Personal', true, now(), now()
                FROM financial_workspace_cutover_plan;
                UPDATE financial_accounts a SET responsible_user_id = m.user_id
                FROM family_members m WHERE a.responsible_user_id = m.id AND a.workspace_id = m.family_id;
                DO $convert$
                DECLARE t text;
                BEGIN
                    FOR t IN SELECT table_name FROM information_schema.columns
                        WHERE table_schema = 'public' AND column_name = 'workspace_id'
                          AND (table_name LIKE 'financial_%' OR table_name = 'service_account_bindings')
                    LOOP
                        EXECUTE format('UPDATE %I x SET workspace_id = p.workspace_id FROM financial_workspace_cutover_plan p WHERE x.workspace_id = p.family_id', t);
                    END LOOP;
                END $convert$;
                DO $restore$
                DECLARE c record;
                BEGIN
                    FOR c IN SELECT * FROM workspace_cutover_fks LOOP
                        EXECUTE format('ALTER TABLE %s ADD CONSTRAINT %I %s', c.relation, c.name, c.definition);
                    END LOOP;
                END $restore$;
                """);

            migrationBuilder.CreateIndex(
                name: "ix_financial_workspaces_owner_user_id",
                table: "financial_workspaces",
                column: "owner_user_id",
                unique: true,
                filter: "type = 'Personal'");

            migrationBuilder.AddForeignKey(
                name: "fk_financial_accounts_financial_workspaces_workspace_id",
                table: "financial_accounts",
                column: "workspace_id",
                principalTable: "financial_workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_accounts_workspace_responsible",
                table: "financial_accounts",
                columns: new[] { "workspace_id", "responsible_user_id" },
                principalTable: "financial_workspaces",
                principalColumns: new[] { "id", "owner_user_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_budgets_financial_workspaces_workspace_id",
                table: "financial_budgets",
                column: "workspace_id",
                principalTable: "financial_workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_categories_financial_workspaces_workspace_id",
                table: "financial_categories",
                column: "workspace_id",
                principalTable: "financial_workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_credit_card_import_batches_financial_workspaces_w",
                table: "financial_credit_card_import_batches",
                column: "workspace_id",
                principalTable: "financial_workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_credit_card_import_items_financial_credit_cards_w",
                table: "financial_credit_card_import_items",
                columns: new[] { "workspace_id", "credit_card_id" },
                principalTable: "financial_credit_cards",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_credit_card_imported_installments_financial_works",
                table: "financial_credit_card_imported_installments",
                column: "workspace_id",
                principalTable: "financial_workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_credit_card_invoice_adjustments_financial_workspa",
                table: "financial_credit_card_invoice_adjustments",
                column: "workspace_id",
                principalTable: "financial_workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_credit_card_invoice_payments_financial_workspaces",
                table: "financial_credit_card_invoice_payments",
                column: "workspace_id",
                principalTable: "financial_workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_credit_card_invoices_financial_credit_cards_works",
                table: "financial_credit_card_invoices",
                columns: new[] { "workspace_id", "credit_card_id" },
                principalTable: "financial_credit_cards",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_credit_card_purchases_financial_workspaces_worksp",
                table: "financial_credit_card_purchases",
                column: "workspace_id",
                principalTable: "financial_workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_credit_cards_financial_workspaces_workspace_id",
                table: "financial_credit_cards",
                column: "workspace_id",
                principalTable: "financial_workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_credit_cards_workspace_holder",
                table: "financial_credit_cards",
                columns: new[] { "workspace_id", "holder_user_id" },
                principalTable: "financial_workspaces",
                principalColumns: new[] { "id", "owner_user_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_debt_installment_reconciliations_financial_worksp",
                table: "financial_debt_installment_reconciliations",
                column: "workspace_id",
                principalTable: "financial_workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_debt_installments_financial_debts_workspace_id_de",
                table: "financial_debt_installments",
                columns: new[] { "workspace_id", "debt_id" },
                principalTable: "financial_debts",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_debt_installments_financial_workspaces_workspace_",
                table: "financial_debt_installments",
                column: "workspace_id",
                principalTable: "financial_workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_debt_payments_financial_accounts_workspace_id_fin",
                table: "financial_debt_payments",
                columns: new[] { "workspace_id", "financial_account_id" },
                principalTable: "financial_accounts",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_debt_payments_financial_debt_installments_workspa",
                table: "financial_debt_payments",
                columns: new[] { "workspace_id", "installment_id" },
                principalTable: "financial_debt_installments",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_debt_payments_financial_transactions_workspace_id",
                table: "financial_debt_payments",
                columns: new[] { "workspace_id", "bank_transaction_id" },
                principalTable: "financial_transactions",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_debt_payments_financial_workspaces_workspace_id",
                table: "financial_debt_payments",
                column: "workspace_id",
                principalTable: "financial_workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_debts_financial_accounts_workspace_id_responsible",
                table: "financial_debts",
                columns: new[] { "workspace_id", "responsible_account_id" },
                principalTable: "financial_accounts",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_debts_financial_workspaces_workspace_id",
                table: "financial_debts",
                column: "workspace_id",
                principalTable: "financial_workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_document_inbox_items_financial_credit_cards_works",
                table: "financial_document_inbox_items",
                columns: new[] { "workspace_id", "credit_card_id" },
                principalTable: "financial_credit_cards",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_document_inbox_items_financial_recurrences_worksp",
                table: "financial_document_inbox_items",
                columns: new[] { "workspace_id", "service_bill_recurrence_id" },
                principalTable: "financial_recurrences",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_document_inbox_items_financial_workspaces_workspa",
                table: "financial_document_inbox_items",
                column: "workspace_id",
                principalTable: "financial_workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_email_collector_checkpoints_financial_workspaces_",
                table: "financial_email_collector_checkpoints",
                column: "workspace_id",
                principalTable: "financial_workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_goal_allocations_financial_accounts_workspace_id_",
                table: "financial_goal_allocations",
                columns: new[] { "workspace_id", "financial_account_id" },
                principalTable: "financial_accounts",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_goal_allocations_financial_goals_workspace_id_goa",
                table: "financial_goal_allocations",
                columns: new[] { "workspace_id", "goal_id" },
                principalTable: "financial_goals",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_goal_allocations_financial_workspaces_workspace_id",
                table: "financial_goal_allocations",
                column: "workspace_id",
                principalTable: "financial_workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_goals_financial_workspaces_workspace_id",
                table: "financial_goals",
                column: "workspace_id",
                principalTable: "financial_workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_planned_invoice_reconciliations_financial_workspa",
                table: "financial_planned_invoice_reconciliations",
                column: "workspace_id",
                principalTable: "financial_workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_planned_transactions_financial_workspaces_workspa",
                table: "financial_planned_transactions",
                column: "workspace_id",
                principalTable: "financial_workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_planning_scenarios_financial_accounts_workspace_i",
                table: "financial_planning_scenarios",
                columns: new[] { "workspace_id", "recovery_account_id" },
                principalTable: "financial_accounts",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_planning_scenarios_financial_workspaces_workspace",
                table: "financial_planning_scenarios",
                column: "workspace_id",
                principalTable: "financial_workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_recurrence_materializations_financial_planned_tra",
                table: "financial_recurrence_materializations",
                columns: new[] { "workspace_id", "planned_transaction_id" },
                principalTable: "financial_planned_transactions",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_recurrence_materializations_financial_workspaces_",
                table: "financial_recurrence_materializations",
                column: "workspace_id",
                principalTable: "financial_workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_recurrences_financial_accounts_workspace_id_finan",
                table: "financial_recurrences",
                columns: new[] { "workspace_id", "financial_account_id" },
                principalTable: "financial_accounts",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_recurrences_financial_categories_workspace_id_fin",
                table: "financial_recurrences",
                columns: new[] { "workspace_id", "financial_category_id", "type" },
                principalTable: "financial_categories",
                principalColumns: new[] { "workspace_id", "id", "type" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_recurrences_financial_workspaces_workspace_id",
                table: "financial_recurrences",
                column: "workspace_id",
                principalTable: "financial_workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_transactions_financial_workspaces_workspace_id",
                table: "financial_transactions",
                column: "workspace_id",
                principalTable: "financial_workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_transfers_financial_workspaces_workspace_id",
                table: "financial_transfers",
                column: "workspace_id",
                principalTable: "financial_workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_service_account_bindings_financial_recurrences_workspace_id",
                table: "service_account_bindings",
                columns: new[] { "workspace_id", "recurring_template_id" },
                principalTable: "financial_recurrences",
                principalColumns: new[] { "workspace_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_service_account_bindings_financial_workspaces_workspace_id",
                table: "service_account_bindings",
                column: "workspace_id",
                principalTable: "financial_workspaces",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $guard$
                BEGIN
                    IF EXISTS (SELECT 1 FROM financial_workspaces) THEN
                        RAISE EXCEPTION 'WORKSPACE_DOWN_BLOCKED: preserve workspace data; use an approved recovery procedure';
                    END IF;
                END $guard$;
                """);
            migrationBuilder.DropForeignKey(
                name: "fk_financial_accounts_financial_workspaces_workspace_id",
                table: "financial_accounts");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_accounts_workspace_responsible",
                table: "financial_accounts");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_budgets_financial_workspaces_workspace_id",
                table: "financial_budgets");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_categories_financial_workspaces_workspace_id",
                table: "financial_categories");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_credit_card_import_batches_financial_workspaces_w",
                table: "financial_credit_card_import_batches");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_credit_card_import_items_financial_credit_cards_w",
                table: "financial_credit_card_import_items");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_credit_card_imported_installments_financial_works",
                table: "financial_credit_card_imported_installments");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_credit_card_invoice_adjustments_financial_workspa",
                table: "financial_credit_card_invoice_adjustments");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_credit_card_invoice_payments_financial_workspaces",
                table: "financial_credit_card_invoice_payments");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_credit_card_invoices_financial_credit_cards_works",
                table: "financial_credit_card_invoices");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_credit_card_purchases_financial_workspaces_worksp",
                table: "financial_credit_card_purchases");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_credit_cards_financial_workspaces_workspace_id",
                table: "financial_credit_cards");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_credit_cards_workspace_holder",
                table: "financial_credit_cards");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_debt_installment_reconciliations_financial_worksp",
                table: "financial_debt_installment_reconciliations");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_debt_installments_financial_debts_workspace_id_de",
                table: "financial_debt_installments");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_debt_installments_financial_workspaces_workspace_",
                table: "financial_debt_installments");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_debt_payments_financial_accounts_workspace_id_fin",
                table: "financial_debt_payments");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_debt_payments_financial_debt_installments_workspa",
                table: "financial_debt_payments");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_debt_payments_financial_transactions_workspace_id",
                table: "financial_debt_payments");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_debt_payments_financial_workspaces_workspace_id",
                table: "financial_debt_payments");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_debts_financial_accounts_workspace_id_responsible",
                table: "financial_debts");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_debts_financial_workspaces_workspace_id",
                table: "financial_debts");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_document_inbox_items_financial_credit_cards_works",
                table: "financial_document_inbox_items");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_document_inbox_items_financial_recurrences_worksp",
                table: "financial_document_inbox_items");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_document_inbox_items_financial_workspaces_workspa",
                table: "financial_document_inbox_items");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_email_collector_checkpoints_financial_workspaces_",
                table: "financial_email_collector_checkpoints");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_goal_allocations_financial_accounts_workspace_id_",
                table: "financial_goal_allocations");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_goal_allocations_financial_goals_workspace_id_goa",
                table: "financial_goal_allocations");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_goal_allocations_financial_workspaces_workspace_id",
                table: "financial_goal_allocations");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_goals_financial_workspaces_workspace_id",
                table: "financial_goals");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_planned_invoice_reconciliations_financial_workspa",
                table: "financial_planned_invoice_reconciliations");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_planned_transactions_financial_workspaces_workspa",
                table: "financial_planned_transactions");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_planning_scenarios_financial_accounts_workspace_i",
                table: "financial_planning_scenarios");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_planning_scenarios_financial_workspaces_workspace",
                table: "financial_planning_scenarios");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_recurrence_materializations_financial_planned_tra",
                table: "financial_recurrence_materializations");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_recurrence_materializations_financial_workspaces_",
                table: "financial_recurrence_materializations");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_recurrences_financial_accounts_workspace_id_finan",
                table: "financial_recurrences");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_recurrences_financial_categories_workspace_id_fin",
                table: "financial_recurrences");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_recurrences_financial_workspaces_workspace_id",
                table: "financial_recurrences");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_transactions_financial_workspaces_workspace_id",
                table: "financial_transactions");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_transfers_financial_workspaces_workspace_id",
                table: "financial_transfers");

            migrationBuilder.DropForeignKey(
                name: "fk_service_account_bindings_financial_recurrences_workspace_id",
                table: "service_account_bindings");

            migrationBuilder.DropForeignKey(
                name: "fk_service_account_bindings_financial_workspaces_workspace_id",
                table: "service_account_bindings");

            migrationBuilder.DropTable(
                name: "financial_workspaces");

            migrationBuilder.Sql("ALTER TABLE financial_transactions RENAME CONSTRAINT ak_financial_transactions_workspace_id_id TO ak_financial_transactions_family_id_id;");

            migrationBuilder.Sql("ALTER TABLE financial_recurrences RENAME CONSTRAINT ak_financial_recurrences_workspace_id_id TO ak_financial_recurrences_family_id_id;");

            migrationBuilder.DropIndex(
                name: "ix_financial_recurrence_materializations_workspace_id_planned_",
                table: "financial_recurrence_materializations");

            migrationBuilder.Sql("ALTER TABLE financial_planning_scenarios RENAME CONSTRAINT ak_financial_planning_scenarios_workspace_id_id TO ak_financial_planning_scenarios_family_id_id;");

            migrationBuilder.Sql("ALTER TABLE financial_planned_transactions RENAME CONSTRAINT ak_financial_planned_transactions_workspace_id_id TO ak_financial_planned_transactions_family_id_id;");

            migrationBuilder.Sql("ALTER TABLE financial_goals RENAME CONSTRAINT ak_financial_goals_workspace_id_id TO ak_financial_goals_family_id_id;");

            migrationBuilder.Sql("ALTER TABLE financial_goal_allocations RENAME CONSTRAINT ak_financial_goal_allocations_workspace_id_id TO ak_financial_goal_allocations_family_id_id;");

            migrationBuilder.Sql("ALTER TABLE financial_debts RENAME CONSTRAINT ak_financial_debts_workspace_id_id TO ak_financial_debts_family_id_id;");

            migrationBuilder.Sql("ALTER TABLE financial_debt_installments RENAME CONSTRAINT ak_financial_debt_installments_workspace_id_id TO ak_financial_debt_installments_family_id_id;");

            migrationBuilder.Sql("ALTER TABLE financial_credit_cards RENAME CONSTRAINT ak_financial_credit_cards_workspace_id_id TO ak_financial_credit_cards_family_id_id;");

            migrationBuilder.Sql("ALTER TABLE financial_credit_card_purchases RENAME CONSTRAINT ak_financial_credit_card_purchases_workspace_id_id TO ak_financial_credit_card_purchases_family_id_id;");

            migrationBuilder.Sql("ALTER TABLE financial_credit_card_invoices RENAME CONSTRAINT ak_financial_credit_card_invoices_workspace_id_id TO ak_financial_credit_card_invoices_family_id_id;");

            migrationBuilder.Sql("ALTER TABLE financial_credit_card_installments RENAME CONSTRAINT ak_financial_credit_card_installments_workspace_id_id TO ak_financial_credit_card_installments_family_id_id;");

            migrationBuilder.Sql("ALTER TABLE financial_credit_card_import_batches RENAME CONSTRAINT ak_financial_credit_card_import_batches_workspace_id_id TO ak_financial_credit_card_import_batches_family_id_id;");

            migrationBuilder.Sql("ALTER TABLE financial_categories RENAME CONSTRAINT ak_financial_categories_workspace_id_id TO ak_financial_categories_family_id_id;");

            migrationBuilder.Sql("ALTER TABLE financial_categories RENAME CONSTRAINT ak_financial_categories_workspace_id_id_type TO ak_financial_categories_family_id_id_type;");

            migrationBuilder.Sql("ALTER TABLE financial_budgets RENAME CONSTRAINT ak_financial_budgets_workspace_id_id TO ak_financial_budgets_family_id_id;");

            migrationBuilder.Sql("ALTER TABLE financial_accounts RENAME CONSTRAINT ak_financial_accounts_workspace_id_id TO ak_financial_accounts_family_id_id;");

            migrationBuilder.RenameColumn(
                name: "workspace_id",
                table: "service_account_bindings",
                newName: "family_id");

            migrationBuilder.RenameIndex(
                name: "ix_service_account_bindings_workspace_id_recurring_template_id",
                table: "service_account_bindings",
                newName: "ix_service_account_bindings_family_id_recurring_template_id");

            migrationBuilder.RenameIndex(
                name: "ix_service_account_bindings_workspace_id_document_type_service",
                table: "service_account_bindings",
                newName: "ix_service_account_bindings_family_id_document_type_service_re");

            migrationBuilder.RenameIndex(
                name: "ix_service_account_bindings_workspace_id_document_type_account",
                table: "service_account_bindings",
                newName: "ix_service_account_bindings_family_id_document_type_account_re");

            migrationBuilder.RenameColumn(
                name: "workspace_id",
                table: "financial_transfers",
                newName: "family_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_transfers_workspace_id_transfer_date_id",
                table: "financial_transfers",
                newName: "ix_financial_transfers_family_id_transfer_date_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_transfers_workspace_id_source_account_id",
                table: "financial_transfers",
                newName: "ix_financial_transfers_family_id_source_account_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_transfers_workspace_id_destination_account_id",
                table: "financial_transfers",
                newName: "ix_financial_transfers_family_id_destination_account_id");

            migrationBuilder.RenameColumn(
                name: "workspace_id",
                table: "financial_transactions",
                newName: "family_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_transactions_workspace_id_transaction_date_id",
                table: "financial_transactions",
                newName: "ix_financial_transactions_family_id_transaction_date_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_transactions_workspace_id_planned_transaction_id",
                table: "financial_transactions",
                newName: "ix_financial_transactions_family_id_planned_transaction_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_transactions_workspace_id_category_id_type",
                table: "financial_transactions",
                newName: "ix_financial_transactions_family_id_category_id_type");

            migrationBuilder.RenameIndex(
                name: "ix_financial_transactions_workspace_id_account_id",
                table: "financial_transactions",
                newName: "ix_financial_transactions_family_id_account_id");

            migrationBuilder.RenameColumn(
                name: "workspace_id",
                table: "financial_recurrences",
                newName: "family_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_recurrences_workspace_id_status_start_date",
                table: "financial_recurrences",
                newName: "ix_financial_recurrences_family_id_status_start_date");

            migrationBuilder.RenameIndex(
                name: "ix_financial_recurrences_workspace_id_financial_category_id_ty",
                table: "financial_recurrences",
                newName: "ix_financial_recurrences_family_id_financial_category_id_type");

            migrationBuilder.RenameIndex(
                name: "ix_financial_recurrences_workspace_id_financial_account_id",
                table: "financial_recurrences",
                newName: "ix_financial_recurrences_family_id_financial_account_id");

            migrationBuilder.RenameColumn(
                name: "workspace_id",
                table: "financial_recurrence_materializations",
                newName: "family_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_recurrence_materializations_workspace_id_recurren1",
                table: "financial_recurrence_materializations",
                newName: "ix_financial_recurrence_materializations_family_id_recurrence_1");

            migrationBuilder.RenameIndex(
                name: "ix_financial_recurrence_materializations_workspace_id_recurren",
                table: "financial_recurrence_materializations",
                newName: "ix_financial_recurrence_materializations_family_id_recurrence_");

            migrationBuilder.RenameColumn(
                name: "workspace_id",
                table: "financial_planning_scenarios",
                newName: "family_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_planning_scenarios_workspace_id_status_start_month",
                table: "financial_planning_scenarios",
                newName: "ix_financial_planning_scenarios_family_id_status_start_month");

            migrationBuilder.RenameIndex(
                name: "ix_financial_planning_scenarios_workspace_id_recovery_account_",
                table: "financial_planning_scenarios",
                newName: "ix_financial_planning_scenarios_family_id_recovery_account_id");

            migrationBuilder.RenameColumn(
                name: "workspace_id",
                table: "financial_planning_monthly_parameters",
                newName: "family_id");

            migrationBuilder.RenameColumn(
                name: "workspace_id",
                table: "financial_planned_transactions",
                newName: "family_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_planned_transactions_workspace_id_due_date_id",
                table: "financial_planned_transactions",
                newName: "ix_financial_planned_transactions_family_id_due_date_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_planned_transactions_workspace_id_category_id_type",
                table: "financial_planned_transactions",
                newName: "ix_financial_planned_transactions_family_id_category_id_type");

            migrationBuilder.RenameIndex(
                name: "ix_financial_planned_transactions_workspace_id_account_id",
                table: "financial_planned_transactions",
                newName: "ix_financial_planned_transactions_family_id_account_id");

            migrationBuilder.RenameColumn(
                name: "workspace_id",
                table: "financial_planned_invoice_reconciliations",
                newName: "family_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_planned_invoice_reconciliations_workspace_id_plan",
                table: "financial_planned_invoice_reconciliations",
                newName: "ix_financial_planned_invoice_reconciliations_family_id_planned");

            migrationBuilder.RenameIndex(
                name: "ix_financial_planned_invoice_reconciliations_workspace_id_cred",
                table: "financial_planned_invoice_reconciliations",
                newName: "ix_financial_planned_invoice_reconciliations_family_id_credit_");

            migrationBuilder.RenameIndex(
                name: "ix_financial_planned_invoice_reconciliations_workspace_id_crea",
                table: "financial_planned_invoice_reconciliations",
                newName: "ix_financial_planned_invoice_reconciliations_family_id_created");

            migrationBuilder.RenameColumn(
                name: "workspace_id",
                table: "financial_goals",
                newName: "family_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_goals_workspace_id_status_target_date",
                table: "financial_goals",
                newName: "ix_financial_goals_family_id_status_target_date");

            migrationBuilder.RenameColumn(
                name: "workspace_id",
                table: "financial_goal_allocations",
                newName: "family_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_goal_allocations_workspace_id_operation_id",
                table: "financial_goal_allocations",
                newName: "ix_financial_goal_allocations_family_id_operation_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_goal_allocations_workspace_id_goal_id_effective_d",
                table: "financial_goal_allocations",
                newName: "ix_financial_goal_allocations_family_id_goal_id_effective_date");

            migrationBuilder.RenameIndex(
                name: "ix_financial_goal_allocations_workspace_id_financial_account_i",
                table: "financial_goal_allocations",
                newName: "ix_financial_goal_allocations_family_id_financial_account_id_c");

            migrationBuilder.RenameColumn(
                name: "workspace_id",
                table: "financial_email_collector_checkpoints",
                newName: "family_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_email_collector_checkpoints_workspace_id_profile_",
                table: "financial_email_collector_checkpoints",
                newName: "ix_financial_email_collector_checkpoints_family_id_profile_name");

            migrationBuilder.RenameColumn(
                name: "workspace_id",
                table: "financial_document_inbox_items",
                newName: "family_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_document_inbox_items_workspace_id_status_received",
                table: "financial_document_inbox_items",
                newName: "ix_financial_document_inbox_items_family_id_status_received_at");

            migrationBuilder.RenameIndex(
                name: "ix_financial_document_inbox_items_workspace_id_source_external",
                table: "financial_document_inbox_items",
                newName: "ix_financial_document_inbox_items_family_id_source_external_so");

            migrationBuilder.RenameIndex(
                name: "ix_financial_document_inbox_items_workspace_id_service_bill_re",
                table: "financial_document_inbox_items",
                newName: "ix_financial_document_inbox_items_family_id_service_bill_recur");

            migrationBuilder.RenameIndex(
                name: "ix_financial_document_inbox_items_workspace_id_service_bill_oc",
                table: "financial_document_inbox_items",
                newName: "ix_financial_document_inbox_items_family_id_service_bill_occur");

            migrationBuilder.RenameIndex(
                name: "ix_financial_document_inbox_items_workspace_id_credit_card_id",
                table: "financial_document_inbox_items",
                newName: "ix_financial_document_inbox_items_family_id_credit_card_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_document_inbox_items_workspace_id_content_hash",
                table: "financial_document_inbox_items",
                newName: "ix_financial_document_inbox_items_family_id_content_hash");

            migrationBuilder.RenameColumn(
                name: "workspace_id",
                table: "financial_debts",
                newName: "family_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_debts_workspace_id_status",
                table: "financial_debts",
                newName: "ix_financial_debts_family_id_status");

            migrationBuilder.RenameIndex(
                name: "ix_financial_debts_workspace_id_responsible_account_id",
                table: "financial_debts",
                newName: "ix_financial_debts_family_id_responsible_account_id");

            migrationBuilder.RenameColumn(
                name: "workspace_id",
                table: "financial_debt_payments",
                newName: "family_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_debt_payments_workspace_id_operation_id",
                table: "financial_debt_payments",
                newName: "ix_financial_debt_payments_family_id_operation_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_debt_payments_workspace_id_installment_id",
                table: "financial_debt_payments",
                newName: "ix_financial_debt_payments_family_id_installment_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_debt_payments_workspace_id_financial_account_id",
                table: "financial_debt_payments",
                newName: "ix_financial_debt_payments_family_id_financial_account_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_debt_payments_workspace_id_bank_transaction_id",
                table: "financial_debt_payments",
                newName: "ix_financial_debt_payments_family_id_bank_transaction_id");

            migrationBuilder.RenameColumn(
                name: "workspace_id",
                table: "financial_debt_installments",
                newName: "family_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_debt_installments_workspace_id_due_date",
                table: "financial_debt_installments",
                newName: "ix_financial_debt_installments_family_id_due_date");

            migrationBuilder.RenameIndex(
                name: "ix_financial_debt_installments_workspace_id_debt_id_installmen",
                table: "financial_debt_installments",
                newName: "ix_financial_debt_installments_family_id_debt_id_installment_n");

            migrationBuilder.RenameColumn(
                name: "workspace_id",
                table: "financial_debt_installment_reconciliations",
                newName: "family_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_debt_installment_reconciliations_workspace_id_pla",
                table: "financial_debt_installment_reconciliations",
                newName: "ix_financial_debt_installment_reconciliations_family_id_planne");

            migrationBuilder.RenameIndex(
                name: "ix_financial_debt_installment_reconciliations_workspace_id_deb",
                table: "financial_debt_installment_reconciliations",
                newName: "ix_financial_debt_installment_reconciliations_family_id_debt_i");

            migrationBuilder.RenameColumn(
                name: "workspace_id",
                table: "financial_credit_cards",
                newName: "family_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_cards_workspace_id_is_archived",
                table: "financial_credit_cards",
                newName: "ix_financial_credit_cards_family_id_is_archived");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_cards_workspace_id_holder_user_id",
                table: "financial_credit_cards",
                newName: "ix_financial_credit_cards_family_id_holder_user_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_cards_workspace_id_default_payment_account",
                table: "financial_credit_cards",
                newName: "ix_financial_credit_cards_family_id_default_payment_account_id");

            migrationBuilder.RenameColumn(
                name: "workspace_id",
                table: "financial_credit_card_purchases",
                newName: "family_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_card_purchases_workspace_id_credit_card_id",
                table: "financial_credit_card_purchases",
                newName: "ix_financial_credit_card_purchases_family_id_credit_card_id_pu");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_card_purchases_workspace_id_category_id",
                table: "financial_credit_card_purchases",
                newName: "ix_financial_credit_card_purchases_family_id_category_id");

            migrationBuilder.RenameColumn(
                name: "workspace_id",
                table: "financial_credit_card_invoices",
                newName: "family_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_card_invoices_workspace_id_credit_card_id_",
                table: "financial_credit_card_invoices",
                newName: "ix_financial_credit_card_invoices_family_id_credit_card_id_clo");

            migrationBuilder.RenameColumn(
                name: "workspace_id",
                table: "financial_credit_card_invoice_payments",
                newName: "family_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_card_invoice_payments_workspace_id_transac",
                table: "financial_credit_card_invoice_payments",
                newName: "ix_financial_credit_card_invoice_payments_family_id_transactio");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_card_invoice_payments_workspace_id_operati",
                table: "financial_credit_card_invoice_payments",
                newName: "ix_financial_credit_card_invoice_payments_family_id_operation_");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_card_invoice_payments_workspace_id_invoice",
                table: "financial_credit_card_invoice_payments",
                newName: "ix_financial_credit_card_invoice_payments_family_id_invoice_id");

            migrationBuilder.RenameColumn(
                name: "workspace_id",
                table: "financial_credit_card_invoice_adjustments",
                newName: "family_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_card_invoice_adjustments_workspace_id_invo",
                table: "financial_credit_card_invoice_adjustments",
                newName: "ix_financial_credit_card_invoice_adjustments_family_id_invoice");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_card_invoice_adjustments_workspace_id_cred",
                table: "financial_credit_card_invoice_adjustments",
                newName: "ix_financial_credit_card_invoice_adjustments_family_id_credit_");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_card_invoice_adjustments_workspace_id_cate",
                table: "financial_credit_card_invoice_adjustments",
                newName: "ix_financial_credit_card_invoice_adjustments_family_id_categor");

            migrationBuilder.RenameColumn(
                name: "workspace_id",
                table: "financial_credit_card_installments",
                newName: "family_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_card_installments_workspace_id_purchase_id",
                table: "financial_credit_card_installments",
                newName: "ix_financial_credit_card_installments_family_id_purchase_id_in");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_card_installments_workspace_id_invoice_id",
                table: "financial_credit_card_installments",
                newName: "ix_financial_credit_card_installments_family_id_invoice_id");

            migrationBuilder.RenameColumn(
                name: "workspace_id",
                table: "financial_credit_card_imported_installments",
                newName: "family_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_card_imported_installments_workspace_id_li",
                table: "financial_credit_card_imported_installments",
                newName: "ix_financial_credit_card_imported_installments_family_id_linke");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_card_imported_installments_workspace_id_in",
                table: "financial_credit_card_imported_installments",
                newName: "ix_financial_credit_card_imported_installments_family_id_invoi");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_card_imported_installments_workspace_id_cr1",
                table: "financial_credit_card_imported_installments",
                newName: "ix_financial_credit_card_imported_installments_family_id_credi1");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_card_imported_installments_workspace_id_cr",
                table: "financial_credit_card_imported_installments",
                newName: "ix_financial_credit_card_imported_installments_family_id_credi");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_card_imported_installments_workspace_id_ca",
                table: "financial_credit_card_imported_installments",
                newName: "ix_financial_credit_card_imported_installments_family_id_categ");

            migrationBuilder.RenameColumn(
                name: "workspace_id",
                table: "financial_credit_card_import_items",
                newName: "family_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_card_import_items_workspace_id_purchase_id",
                table: "financial_credit_card_import_items",
                newName: "ix_financial_credit_card_import_items_family_id_purchase_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_card_import_items_workspace_id_import_batc",
                table: "financial_credit_card_import_items",
                newName: "ix_financial_credit_card_import_items_family_id_import_batch_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_card_import_items_workspace_id_credit_card",
                table: "financial_credit_card_import_items",
                newName: "ix_financial_credit_card_import_items_family_id_credit_card_id");

            migrationBuilder.RenameColumn(
                name: "workspace_id",
                table: "financial_credit_card_import_batches",
                newName: "family_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_credit_card_import_batches_workspace_id_credit_ca",
                table: "financial_credit_card_import_batches",
                newName: "ix_financial_credit_card_import_batches_family_id_credit_card_");

            migrationBuilder.RenameColumn(
                name: "workspace_id",
                table: "financial_categories",
                newName: "family_id");

            migrationBuilder.RenameIndex(
                name: "ux_financial_categories_workspace_id_type_name",
                table: "financial_categories",
                newName: "ux_financial_categories_family_id_type_name");

            migrationBuilder.RenameColumn(
                name: "workspace_id",
                table: "financial_budgets",
                newName: "family_id");

            migrationBuilder.RenameIndex(
                name: "ux_financial_budgets_workspace_id_competence",
                table: "financial_budgets",
                newName: "ux_financial_budgets_family_id_competence");

            migrationBuilder.RenameColumn(
                name: "workspace_id",
                table: "financial_budget_targets",
                newName: "family_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_budget_targets_workspace_id_category_id_category_",
                table: "financial_budget_targets",
                newName: "ix_financial_budget_targets_family_id_category_id_category_type");

            migrationBuilder.RenameIndex(
                name: "ix_financial_budget_targets_workspace_id_budget_id",
                table: "financial_budget_targets",
                newName: "ix_financial_budget_targets_family_id_budget_id");

            migrationBuilder.RenameColumn(
                name: "workspace_id",
                table: "financial_accounts",
                newName: "family_id");

            migrationBuilder.RenameColumn(
                name: "responsible_user_id",
                table: "financial_accounts",
                newName: "responsible_member_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_accounts_workspace_id_responsible_user_id",
                table: "financial_accounts",
                newName: "ix_financial_accounts_family_id_responsible_member_id");

            migrationBuilder.RenameIndex(
                name: "ix_financial_accounts_workspace_id_is_active",
                table: "financial_accounts",
                newName: "ix_financial_accounts_family_id_is_active");



































            migrationBuilder.AddUniqueConstraint(
                name: "ak_family_members_family_id_id",
                table: "family_members",
                columns: new[] { "family_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_recurrence_materializations_planned_transaction_id",
                table: "financial_recurrence_materializations",
                column: "planned_transaction_id");

            migrationBuilder.AddForeignKey(
                name: "fk_financial_accounts_families_family_id",
                table: "financial_accounts",
                column: "family_id",
                principalTable: "families",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_accounts_family_members_responsible",
                table: "financial_accounts",
                columns: new[] { "family_id", "responsible_member_id" },
                principalTable: "family_members",
                principalColumns: new[] { "family_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_budgets_families_family_id",
                table: "financial_budgets",
                column: "family_id",
                principalTable: "families",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_categories_families_family_id",
                table: "financial_categories",
                column: "family_id",
                principalTable: "families",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_credit_card_import_batches_families_family_id",
                table: "financial_credit_card_import_batches",
                column: "family_id",
                principalTable: "families",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_credit_card_import_items_financial_credit_cards_f",
                table: "financial_credit_card_import_items",
                columns: new[] { "family_id", "credit_card_id" },
                principalTable: "financial_credit_cards",
                principalColumns: new[] { "family_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_credit_card_imported_installments_families_family",
                table: "financial_credit_card_imported_installments",
                column: "family_id",
                principalTable: "families",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_credit_card_invoice_adjustments_families_family_id",
                table: "financial_credit_card_invoice_adjustments",
                column: "family_id",
                principalTable: "families",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_credit_card_invoice_payments_families_family_id",
                table: "financial_credit_card_invoice_payments",
                column: "family_id",
                principalTable: "families",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_credit_card_invoices_financial_credit_cards_famil",
                table: "financial_credit_card_invoices",
                columns: new[] { "family_id", "credit_card_id" },
                principalTable: "financial_credit_cards",
                principalColumns: new[] { "family_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_credit_card_purchases_families_family_id",
                table: "financial_credit_card_purchases",
                column: "family_id",
                principalTable: "families",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_credit_cards_families_family_id",
                table: "financial_credit_cards",
                column: "family_id",
                principalTable: "families",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_credit_cards_family_members_holder",
                table: "financial_credit_cards",
                columns: new[] { "family_id", "holder_user_id" },
                principalTable: "family_members",
                principalColumns: new[] { "family_id", "user_id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_debt_installment_reconciliations_families_family_",
                table: "financial_debt_installment_reconciliations",
                column: "family_id",
                principalTable: "families",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_debt_installments_families_family_id",
                table: "financial_debt_installments",
                column: "family_id",
                principalTable: "families",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_debt_installments_financial_debts_family_id_debt_",
                table: "financial_debt_installments",
                columns: new[] { "family_id", "debt_id" },
                principalTable: "financial_debts",
                principalColumns: new[] { "family_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_debt_payments_families_family_id",
                table: "financial_debt_payments",
                column: "family_id",
                principalTable: "families",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_debt_payments_financial_accounts_family_id_financ",
                table: "financial_debt_payments",
                columns: new[] { "family_id", "financial_account_id" },
                principalTable: "financial_accounts",
                principalColumns: new[] { "family_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_debt_payments_financial_debt_installments_family_",
                table: "financial_debt_payments",
                columns: new[] { "family_id", "installment_id" },
                principalTable: "financial_debt_installments",
                principalColumns: new[] { "family_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_debt_payments_financial_transactions_family_id_ba",
                table: "financial_debt_payments",
                columns: new[] { "family_id", "bank_transaction_id" },
                principalTable: "financial_transactions",
                principalColumns: new[] { "family_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_debts_families_family_id",
                table: "financial_debts",
                column: "family_id",
                principalTable: "families",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_debts_financial_accounts_family_id_responsible_ac",
                table: "financial_debts",
                columns: new[] { "family_id", "responsible_account_id" },
                principalTable: "financial_accounts",
                principalColumns: new[] { "family_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_document_inbox_items_families_family_id",
                table: "financial_document_inbox_items",
                column: "family_id",
                principalTable: "families",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_document_inbox_items_financial_credit_cards_famil",
                table: "financial_document_inbox_items",
                columns: new[] { "family_id", "credit_card_id" },
                principalTable: "financial_credit_cards",
                principalColumns: new[] { "family_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_document_inbox_items_financial_recurrences_family",
                table: "financial_document_inbox_items",
                columns: new[] { "family_id", "service_bill_recurrence_id" },
                principalTable: "financial_recurrences",
                principalColumns: new[] { "family_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_email_collector_checkpoints_families_family_id",
                table: "financial_email_collector_checkpoints",
                column: "family_id",
                principalTable: "families",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_goal_allocations_families_family_id",
                table: "financial_goal_allocations",
                column: "family_id",
                principalTable: "families",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_goal_allocations_financial_accounts_family_id_fin",
                table: "financial_goal_allocations",
                columns: new[] { "family_id", "financial_account_id" },
                principalTable: "financial_accounts",
                principalColumns: new[] { "family_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_goal_allocations_financial_goals_family_id_goal_id",
                table: "financial_goal_allocations",
                columns: new[] { "family_id", "goal_id" },
                principalTable: "financial_goals",
                principalColumns: new[] { "family_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_goals_families_family_id",
                table: "financial_goals",
                column: "family_id",
                principalTable: "families",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_planned_invoice_reconciliations_families_family_id",
                table: "financial_planned_invoice_reconciliations",
                column: "family_id",
                principalTable: "families",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_planned_transactions_families_family_id",
                table: "financial_planned_transactions",
                column: "family_id",
                principalTable: "families",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_planning_scenarios_families_family_id",
                table: "financial_planning_scenarios",
                column: "family_id",
                principalTable: "families",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_planning_scenarios_financial_accounts_family_id_r",
                table: "financial_planning_scenarios",
                columns: new[] { "family_id", "recovery_account_id" },
                principalTable: "financial_accounts",
                principalColumns: new[] { "family_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_recurrence_materializations_families_family_id",
                table: "financial_recurrence_materializations",
                column: "family_id",
                principalTable: "families",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_recurrence_materializations_financial_planned_tra",
                table: "financial_recurrence_materializations",
                column: "planned_transaction_id",
                principalTable: "financial_planned_transactions",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_recurrences_families_family_id",
                table: "financial_recurrences",
                column: "family_id",
                principalTable: "families",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_recurrences_financial_accounts_family_id_financia",
                table: "financial_recurrences",
                columns: new[] { "family_id", "financial_account_id" },
                principalTable: "financial_accounts",
                principalColumns: new[] { "family_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_recurrences_financial_categories_family_id_financ",
                table: "financial_recurrences",
                columns: new[] { "family_id", "financial_category_id", "type" },
                principalTable: "financial_categories",
                principalColumns: new[] { "family_id", "id", "type" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_transactions_families_family_id",
                table: "financial_transactions",
                column: "family_id",
                principalTable: "families",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_transfers_families_family_id",
                table: "financial_transfers",
                column: "family_id",
                principalTable: "families",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_service_account_bindings_families_family_id",
                table: "service_account_bindings",
                column: "family_id",
                principalTable: "families",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_service_account_bindings_financial_recurrences_family_id_re",
                table: "service_account_bindings",
                columns: new[] { "family_id", "recurring_template_id" },
                principalTable: "financial_recurrences",
                principalColumns: new[] { "family_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }
    }
}
