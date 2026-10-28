using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPluggyFinancialIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pluggy_account_links",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pluggy_account_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    kind = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    financial_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    credit_card_id = table.Column<Guid>(type: "uuid", nullable: true),
                    display_name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    institution_name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    pluggy_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    pluggy_subtype = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    import_from = table.Column<DateOnly>(type: "date", nullable: true),
                    linked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    linked_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_synced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    observed_balance = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    overdraft_contracted_limit = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    overdraft_used_limit = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    credit_limit = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    available_credit_limit = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pluggy_account_links", x => x.id);
                    table.CheckConstraint("ck_pluggy_account_links_kind", "kind IN ('Bank','Card')");
                    table.CheckConstraint("ck_pluggy_account_links_status", "status IN ('Active','ConsentExpired','Attention')");
                    table.CheckConstraint("ck_pluggy_account_links_target", "(kind = 'Bank' AND financial_account_id IS NOT NULL AND credit_card_id IS NULL) OR (kind = 'Card' AND credit_card_id IS NOT NULL AND financial_account_id IS NULL)");
                    table.ForeignKey(
                        name: "fk_pluggy_account_links_accounts_same_workspace",
                        columns: x => new { x.workspace_id, x.financial_account_id },
                        principalTable: "financial_accounts",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pluggy_account_links_application_users_linked_by_user_id",
                        column: x => x.linked_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pluggy_account_links_cards_same_workspace",
                        columns: x => new { x.workspace_id, x.credit_card_id },
                        principalTable: "financial_credit_cards",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pluggy_account_links_financial_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "financial_workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pluggy_account_links_pluggy_item_links_connection_id",
                        column: x => x.connection_id,
                        principalTable: "pluggy_item_links",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "pluggy_postings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    imported_transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_link_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    financial_transaction_id = table.Column<Guid>(type: "uuid", nullable: true),
                    financial_transfer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    credit_card_purchase_id = table.Column<Guid>(type: "uuid", nullable: true),
                    credit_card_invoice_payment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    counterpart_posting_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolution = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    category_source = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    applied_amount = table.Column<decimal>(type: "numeric", nullable: true),
                    applied_date = table.Column<DateOnly>(type: "date", nullable: true),
                    applied_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pluggy_postings", x => x.id);
                    table.CheckConstraint("ck_pluggy_postings_applied_snapshot", "(state = 'Applied') = (applied_amount IS NOT NULL AND applied_date IS NOT NULL)");
                    table.CheckConstraint("ck_pluggy_postings_applied_targets", "(state = 'Applied' AND ((kind = 'BankMovement' AND financial_transaction_id IS NOT NULL) OR (kind = 'Transfer' AND financial_transfer_id IS NOT NULL) OR (kind = 'CardPurchase' AND credit_card_purchase_id IS NOT NULL) OR (kind = 'CardBillPayment' AND financial_transaction_id IS NOT NULL AND credit_card_invoice_payment_id IS NOT NULL))) OR (state <> 'Applied' AND financial_transaction_id IS NULL AND financial_transfer_id IS NULL AND credit_card_purchase_id IS NULL AND credit_card_invoice_payment_id IS NULL)");
                    table.CheckConstraint("ck_pluggy_postings_category_source", "category_source IN ('None','Mapped','User')");
                    table.CheckConstraint("ck_pluggy_postings_kind", "kind IN ('None','BankMovement','Transfer','CardPurchase','CardBillPayment')");
                    table.CheckConstraint("ck_pluggy_postings_resolution", "resolution IS NULL OR resolution IN ('AsIncome','AsExpense','AsCardPurchase','Ignore')");
                    table.CheckConstraint("ck_pluggy_postings_state", "state IN ('WaitingPosted','Applied','NeedsReview','Covered','Ignored')");
                    table.ForeignKey(
                        name: "fk_pluggy_postings_application_users_resolved_by_user_id",
                        column: x => x.resolved_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pluggy_postings_financial_categories_category_id",
                        column: x => x.category_id,
                        principalTable: "financial_categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pluggy_postings_financial_credit_card_invoice_payments_cred",
                        column: x => x.credit_card_invoice_payment_id,
                        principalTable: "financial_credit_card_invoice_payments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pluggy_postings_financial_transfers_financial_transfer_id",
                        column: x => x.financial_transfer_id,
                        principalTable: "financial_transfers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pluggy_postings_financial_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "financial_workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pluggy_postings_pluggy_account_links_account_link_id",
                        column: x => x.account_link_id,
                        principalTable: "pluggy_account_links",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pluggy_postings_pluggy_imported_transactions_imported_trans",
                        column: x => x.imported_transaction_id,
                        principalTable: "pluggy_imported_transactions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pluggy_postings_pluggy_postings_counterpart_posting_id",
                        column: x => x.counterpart_posting_id,
                        principalTable: "pluggy_postings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pluggy_postings_purchases_same_workspace",
                        columns: x => new { x.workspace_id, x.credit_card_purchase_id },
                        principalTable: "financial_credit_card_purchases",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pluggy_postings_transactions_same_workspace",
                        columns: x => new { x.workspace_id, x.financial_transaction_id },
                        principalTable: "financial_transactions",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_pluggy_account_links_linked_by_user_id",
                table: "pluggy_account_links",
                column: "linked_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_pluggy_account_links_workspace_id_connection_id",
                table: "pluggy_account_links",
                columns: new[] { "workspace_id", "connection_id" });

            migrationBuilder.CreateIndex(
                name: "ix_pluggy_account_links_workspace_id_credit_card_id",
                table: "pluggy_account_links",
                columns: new[] { "workspace_id", "credit_card_id" });

            migrationBuilder.CreateIndex(
                name: "ix_pluggy_account_links_workspace_id_financial_account_id",
                table: "pluggy_account_links",
                columns: new[] { "workspace_id", "financial_account_id" });

            migrationBuilder.CreateIndex(
                name: "ux_pluggy_account_links_credit_card",
                table: "pluggy_account_links",
                column: "credit_card_id",
                unique: true,
                filter: "credit_card_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_pluggy_account_links_external",
                table: "pluggy_account_links",
                columns: new[] { "connection_id", "pluggy_account_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_pluggy_account_links_financial_account",
                table: "pluggy_account_links",
                column: "financial_account_id",
                unique: true,
                filter: "financial_account_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_pluggy_postings_account_link_id_state",
                table: "pluggy_postings",
                columns: new[] { "account_link_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ix_pluggy_postings_category_id",
                table: "pluggy_postings",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_pluggy_postings_counterpart_posting_id",
                table: "pluggy_postings",
                column: "counterpart_posting_id");

            migrationBuilder.CreateIndex(
                name: "ix_pluggy_postings_financial_transfer_id",
                table: "pluggy_postings",
                column: "financial_transfer_id");

            migrationBuilder.CreateIndex(
                name: "ix_pluggy_postings_resolved_by_user_id",
                table: "pluggy_postings",
                column: "resolved_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_pluggy_postings_workspace_id_credit_card_purchase_id",
                table: "pluggy_postings",
                columns: new[] { "workspace_id", "credit_card_purchase_id" });

            migrationBuilder.CreateIndex(
                name: "ix_pluggy_postings_workspace_id_financial_transaction_id",
                table: "pluggy_postings",
                columns: new[] { "workspace_id", "financial_transaction_id" });

            migrationBuilder.CreateIndex(
                name: "ix_pluggy_postings_workspace_id_state",
                table: "pluggy_postings",
                columns: new[] { "workspace_id", "state" });

            migrationBuilder.CreateIndex(
                name: "ux_pluggy_postings_imported",
                table: "pluggy_postings",
                column: "imported_transaction_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_pluggy_postings_invoice_payment",
                table: "pluggy_postings",
                column: "credit_card_invoice_payment_id",
                unique: true,
                filter: "credit_card_invoice_payment_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_pluggy_postings_purchase",
                table: "pluggy_postings",
                column: "credit_card_purchase_id",
                unique: true,
                filter: "credit_card_purchase_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_pluggy_postings_transaction",
                table: "pluggy_postings",
                column: "financial_transaction_id",
                unique: true,
                filter: "financial_transaction_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pluggy_postings");

            migrationBuilder.DropTable(
                name: "pluggy_account_links");
        }
    }
}
