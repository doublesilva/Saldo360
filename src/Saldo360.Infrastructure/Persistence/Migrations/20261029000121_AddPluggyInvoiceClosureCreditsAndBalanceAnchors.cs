using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPluggyInvoiceClosureCreditsAndBalanceAnchors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_pluggy_postings_applied_targets",
                table: "pluggy_postings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_pluggy_postings_kind",
                table: "pluggy_postings");

            migrationBuilder.AddColumn<Guid>(
                name: "credit_card_invoice_adjustment_id",
                table: "pluggy_postings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "components_total",
                table: "pluggy_card_bills",
                type: "numeric(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "finance_charges_total",
                table: "pluggy_card_bills",
                type: "numeric(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "minimum_payment",
                table: "pluggy_card_bills",
                type: "numeric(19,4)",
                precision: 19,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "reconciled_at",
                table: "pluggy_card_bills",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reconciliation",
                table: "pluggy_card_bills",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "NotVerified");

            migrationBuilder.AddColumn<string>(
                name: "reconciliation_reason",
                table: "pluggy_card_bills",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "institution_closed_at",
                table: "financial_credit_card_invoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "institution_source",
                table: "financial_credit_card_invoices",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "institution_total",
                table: "financial_credit_card_invoices",
                type: "numeric",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "financial_account_balance_anchors",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    as_of_date = table.Column<DateOnly>(type: "date", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    source_link_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reliability = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    unavailable_reason = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: true),
                    derived_opening_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    derived_opening_date = table.Column<DateOnly>(type: "date", nullable: true),
                    is_current = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_account_balance_anchors", x => x.id);
                    table.CheckConstraint("ck_balance_anchors_derivation", "(derived_opening_amount IS NULL) = (derived_opening_date IS NULL) AND (reliability <> 'Unreliable' OR derived_opening_amount IS NULL)");
                    table.CheckConstraint("ck_balance_anchors_kind", "kind IN ('CurrentBalance','AvailableBalance','Unknown')");
                    table.CheckConstraint("ck_balance_anchors_reliability", "reliability IN ('Reliable','Provisional','Unreliable')");
                    table.ForeignKey(
                        name: "fk_financial_account_balance_anchors_financial_accounts_accoun",
                        column: x => x.account_id,
                        principalTable: "financial_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_account_balance_anchors_financial_workspaces_work",
                        column: x => x.workspace_id,
                        principalTable: "financial_workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "pluggy_card_bill_payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bill_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    payment_date = table.Column<DateOnly>(type: "date", nullable: false),
                    payment_mode = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    value_type = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pluggy_card_bill_payments", x => x.id);
                    table.CheckConstraint("ck_pluggy_card_bill_payments_amount", "amount > 0");
                    table.ForeignKey(
                        name: "fk_pluggy_card_bill_payments_financial_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "financial_workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pluggy_card_bill_payments_pluggy_card_bills_bill_id",
                        column: x => x.bill_id,
                        principalTable: "pluggy_card_bills",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_pluggy_postings_invoice_adjustment",
                table: "pluggy_postings",
                column: "credit_card_invoice_adjustment_id",
                unique: true,
                filter: "credit_card_invoice_adjustment_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_pluggy_postings_applied_targets",
                table: "pluggy_postings",
                sql: "(state = 'Applied' AND ((kind = 'BankMovement' AND financial_transaction_id IS NOT NULL AND credit_card_invoice_adjustment_id IS NULL) OR (kind = 'Transfer' AND financial_transfer_id IS NOT NULL AND credit_card_invoice_adjustment_id IS NULL) OR (kind = 'CardPurchase' AND credit_card_purchase_id IS NOT NULL AND credit_card_invoice_adjustment_id IS NULL) OR (kind = 'CardBillPayment' AND financial_transaction_id IS NOT NULL AND credit_card_invoice_payment_id IS NOT NULL AND credit_card_invoice_adjustment_id IS NULL) OR (kind = 'CardCredit' AND credit_card_invoice_adjustment_id IS NOT NULL AND financial_transaction_id IS NULL AND financial_transfer_id IS NULL AND credit_card_purchase_id IS NULL AND credit_card_invoice_payment_id IS NULL))) OR (state <> 'Applied' AND financial_transaction_id IS NULL AND financial_transfer_id IS NULL AND credit_card_purchase_id IS NULL AND credit_card_invoice_payment_id IS NULL AND credit_card_invoice_adjustment_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_pluggy_postings_kind",
                table: "pluggy_postings",
                sql: "kind IN ('None','BankMovement','Transfer','CardPurchase','CardBillPayment','CardCredit')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_pluggy_card_bills_reconciliation",
                table: "pluggy_card_bills",
                sql: "reconciliation IN ('NotVerified','Reconciled','Divergent','Incomplete')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_invoice_institution_state",
                table: "financial_credit_card_invoices",
                sql: "(institution_closed_at IS NULL AND institution_total IS NULL AND institution_source IS NULL) OR (institution_closed_at IS NOT NULL AND institution_total >= 0 AND scale(institution_total) <= 2 AND institution_source IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_financial_account_balance_anchors_workspace_id_account_id_o",
                table: "financial_account_balance_anchors",
                columns: new[] { "workspace_id", "account_id", "observed_at" });

            migrationBuilder.CreateIndex(
                name: "ux_balance_anchors_current",
                table: "financial_account_balance_anchors",
                column: "account_id",
                unique: true,
                filter: "is_current");

            migrationBuilder.CreateIndex(
                name: "ix_pluggy_card_bill_payments_workspace_id",
                table: "pluggy_card_bill_payments",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "ux_pluggy_card_bill_payments_ordinal",
                table: "pluggy_card_bill_payments",
                columns: new[] { "bill_id", "ordinal" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_pluggy_postings_financial_credit_card_invoice_adjustments_c",
                table: "pluggy_postings",
                column: "credit_card_invoice_adjustment_id",
                principalTable: "financial_credit_card_invoice_adjustments",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // O pagamento de fatura continua exigindo fatura FECHADA, mas agora também vale o fechamento informado pela instituição (banco), que
            // independe da conciliação das compras. Fatura aberta continua recusada. A mensagem e o código do erro não mudam.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION saldo360_official_payment_guard() RETURNS trigger LANGUAGE plpgsql AS $guard$
                DECLARE version integer; closed timestamptz;
                BEGIN
                    SELECT official_version, institution_closed_at INTO version, closed FROM financial_credit_card_invoices WHERE workspace_id=NEW.workspace_id AND id=NEW.invoice_id FOR UPDATE;
                    IF version IS NULL OR (version=0 AND closed IS NULL) THEN RAISE EXCEPTION 'OFFICIAL_INVOICE_REQUIRED_FOR_PAYMENT' USING ERRCODE='23514'; END IF;
                    RETURN NEW;
                END $guard$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION saldo360_official_payment_guard() RETURNS trigger LANGUAGE plpgsql AS $guard$
                DECLARE version integer;
                BEGIN
                    SELECT official_version INTO version FROM financial_credit_card_invoices WHERE workspace_id=NEW.workspace_id AND id=NEW.invoice_id FOR UPDATE;
                    IF version IS NULL OR version=0 THEN RAISE EXCEPTION 'OFFICIAL_INVOICE_REQUIRED_FOR_PAYMENT' USING ERRCODE='23514'; END IF;
                    RETURN NEW;
                END $guard$;
                """);
            migrationBuilder.DropForeignKey(
                name: "fk_pluggy_postings_financial_credit_card_invoice_adjustments_c",
                table: "pluggy_postings");

            migrationBuilder.DropTable(
                name: "financial_account_balance_anchors");

            migrationBuilder.DropTable(
                name: "pluggy_card_bill_payments");

            migrationBuilder.DropIndex(
                name: "ux_pluggy_postings_invoice_adjustment",
                table: "pluggy_postings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_pluggy_postings_applied_targets",
                table: "pluggy_postings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_pluggy_postings_kind",
                table: "pluggy_postings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_pluggy_card_bills_reconciliation",
                table: "pluggy_card_bills");

            migrationBuilder.DropCheckConstraint(
                name: "ck_invoice_institution_state",
                table: "financial_credit_card_invoices");

            migrationBuilder.DropColumn(
                name: "credit_card_invoice_adjustment_id",
                table: "pluggy_postings");

            migrationBuilder.DropColumn(
                name: "components_total",
                table: "pluggy_card_bills");

            migrationBuilder.DropColumn(
                name: "finance_charges_total",
                table: "pluggy_card_bills");

            migrationBuilder.DropColumn(
                name: "minimum_payment",
                table: "pluggy_card_bills");

            migrationBuilder.DropColumn(
                name: "reconciled_at",
                table: "pluggy_card_bills");

            migrationBuilder.DropColumn(
                name: "reconciliation",
                table: "pluggy_card_bills");

            migrationBuilder.DropColumn(
                name: "reconciliation_reason",
                table: "pluggy_card_bills");

            migrationBuilder.DropColumn(
                name: "institution_closed_at",
                table: "financial_credit_card_invoices");

            migrationBuilder.DropColumn(
                name: "institution_source",
                table: "financial_credit_card_invoices");

            migrationBuilder.DropColumn(
                name: "institution_total",
                table: "financial_credit_card_invoices");

            migrationBuilder.AddCheckConstraint(
                name: "ck_pluggy_postings_applied_targets",
                table: "pluggy_postings",
                sql: "(state = 'Applied' AND ((kind = 'BankMovement' AND financial_transaction_id IS NOT NULL) OR (kind = 'Transfer' AND financial_transfer_id IS NOT NULL) OR (kind = 'CardPurchase' AND credit_card_purchase_id IS NOT NULL) OR (kind = 'CardBillPayment' AND financial_transaction_id IS NOT NULL AND credit_card_invoice_payment_id IS NOT NULL))) OR (state <> 'Applied' AND financial_transaction_id IS NULL AND financial_transfer_id IS NULL AND credit_card_purchase_id IS NULL AND credit_card_invoice_payment_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_pluggy_postings_kind",
                table: "pluggy_postings",
                sql: "kind IN ('None','BankMovement','Transfer','CardPurchase','CardBillPayment')");
        }
    }
}
