using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBankRefunds : Migration
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

            migrationBuilder.CreateTable(
                name: "financial_bank_refunds",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    refund_transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    original_transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric", nullable: false),
                    confirmed_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_bank_refunds", x => x.id);
                    table.CheckConstraint("ck_financial_bank_refunds_amount_positive", "amount > 0");
                    table.CheckConstraint("ck_financial_bank_refunds_cancelled_pair", "(cancelled_at IS NULL) = (cancelled_by_user_id IS NULL)");
                    table.CheckConstraint("ck_financial_bank_refunds_distinct", "refund_transaction_id <> original_transaction_id");
                    table.ForeignKey(
                        name: "fk_financial_bank_refunds_application_users_cancelled_by_user_",
                        column: x => x.cancelled_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_bank_refunds_application_users_confirmed_by_user_",
                        column: x => x.confirmed_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_bank_refunds_financial_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "financial_workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_bank_refunds_original_same_workspace",
                        columns: x => new { x.workspace_id, x.original_transaction_id },
                        principalTable: "financial_transactions",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_bank_refunds_refund_same_workspace",
                        columns: x => new { x.workspace_id, x.refund_transaction_id },
                        principalTable: "financial_transactions",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_pluggy_postings_applied_targets",
                table: "pluggy_postings",
                sql: "(state = 'Applied' AND ((kind = 'BankMovement' AND financial_transaction_id IS NOT NULL AND credit_card_invoice_adjustment_id IS NULL) OR (kind = 'Transfer' AND financial_transfer_id IS NOT NULL AND credit_card_invoice_adjustment_id IS NULL) OR (kind = 'CardPurchase' AND credit_card_purchase_id IS NOT NULL AND credit_card_invoice_adjustment_id IS NULL) OR (kind = 'CardBillPayment' AND financial_transaction_id IS NOT NULL AND credit_card_invoice_payment_id IS NOT NULL AND credit_card_invoice_adjustment_id IS NULL) OR (kind = 'BankRefund' AND financial_transaction_id IS NOT NULL AND credit_card_invoice_adjustment_id IS NULL AND financial_transfer_id IS NULL) OR (kind = 'CardCredit' AND credit_card_invoice_adjustment_id IS NOT NULL AND financial_transaction_id IS NULL AND financial_transfer_id IS NULL AND credit_card_purchase_id IS NULL AND credit_card_invoice_payment_id IS NULL))) OR (state <> 'Applied' AND financial_transaction_id IS NULL AND financial_transfer_id IS NULL AND credit_card_purchase_id IS NULL AND credit_card_invoice_payment_id IS NULL AND credit_card_invoice_adjustment_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_pluggy_postings_kind",
                table: "pluggy_postings",
                sql: "kind IN ('None','BankMovement','Transfer','CardPurchase','CardBillPayment','CardCredit','BankRefund')");

            migrationBuilder.CreateIndex(
                name: "ix_financial_bank_refunds_cancelled_by_user_id",
                table: "financial_bank_refunds",
                column: "cancelled_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_financial_bank_refunds_confirmed_by_user_id",
                table: "financial_bank_refunds",
                column: "confirmed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_financial_bank_refunds_workspace_id_original_transaction_id",
                table: "financial_bank_refunds",
                columns: new[] { "workspace_id", "original_transaction_id" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_bank_refunds_workspace_id_refund_transaction_id",
                table: "financial_bank_refunds",
                columns: new[] { "workspace_id", "refund_transaction_id" });

            migrationBuilder.CreateIndex(
                name: "ux_financial_bank_refunds_active_refund",
                table: "financial_bank_refunds",
                column: "refund_transaction_id",
                unique: true,
                filter: "cancelled_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "financial_bank_refunds");

            migrationBuilder.DropCheckConstraint(
                name: "ck_pluggy_postings_applied_targets",
                table: "pluggy_postings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_pluggy_postings_kind",
                table: "pluggy_postings");

            migrationBuilder.AddCheckConstraint(
                name: "ck_pluggy_postings_applied_targets",
                table: "pluggy_postings",
                sql: "(state = 'Applied' AND ((kind = 'BankMovement' AND financial_transaction_id IS NOT NULL AND credit_card_invoice_adjustment_id IS NULL) OR (kind = 'Transfer' AND financial_transfer_id IS NOT NULL AND credit_card_invoice_adjustment_id IS NULL) OR (kind = 'CardPurchase' AND credit_card_purchase_id IS NOT NULL AND credit_card_invoice_adjustment_id IS NULL) OR (kind = 'CardBillPayment' AND financial_transaction_id IS NOT NULL AND credit_card_invoice_payment_id IS NOT NULL AND credit_card_invoice_adjustment_id IS NULL) OR (kind = 'CardCredit' AND credit_card_invoice_adjustment_id IS NOT NULL AND financial_transaction_id IS NULL AND financial_transfer_id IS NULL AND credit_card_purchase_id IS NULL AND credit_card_invoice_payment_id IS NULL))) OR (state <> 'Applied' AND financial_transaction_id IS NULL AND financial_transfer_id IS NULL AND credit_card_purchase_id IS NULL AND credit_card_invoice_payment_id IS NULL AND credit_card_invoice_adjustment_id IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_pluggy_postings_kind",
                table: "pluggy_postings",
                sql: "kind IN ('None','BankMovement','Transfer','CardPurchase','CardBillPayment','CardCredit')");
        }
    }
}
