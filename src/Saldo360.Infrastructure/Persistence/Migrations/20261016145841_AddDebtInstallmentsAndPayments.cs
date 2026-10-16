using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDebtInstallmentsAndPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "financial_debt_installments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    debt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    installment_number = table.Column<int>(type: "integer", nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: false),
                    scheduled_amount = table.Column<decimal>(type: "numeric", nullable: false),
                    principal_component = table.Column<decimal>(type: "numeric", nullable: true),
                    interest_component = table.Column<decimal>(type: "numeric", nullable: true),
                    insurance_component = table.Column<decimal>(type: "numeric", nullable: true),
                    other_charges_component = table.Column<decimal>(type: "numeric", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_debt_installments", x => x.id);
                    table.UniqueConstraint("ak_financial_debt_installments_family_id_id", x => new { x.family_id, x.id });
                    table.CheckConstraint("ck_debt_installment_amount_positive", "scheduled_amount > 0");
                    table.CheckConstraint("ck_debt_installment_number_positive", "installment_number > 0");
                    table.ForeignKey(
                        name: "fk_financial_debt_installments_families_family_id",
                        column: x => x.family_id,
                        principalTable: "families",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_debt_installments_financial_debts_family_id_debt_",
                        columns: x => new { x.family_id, x.debt_id },
                        principalTable: "financial_debts",
                        principalColumns: new[] { "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "financial_debt_payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    debt_id = table.Column<Guid>(type: "uuid", nullable: false),
                    installment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    financial_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payment_date = table.Column<DateOnly>(type: "date", nullable: false),
                    amount = table.Column<decimal>(type: "numeric", nullable: false),
                    principal_paid = table.Column<decimal>(type: "numeric", nullable: true),
                    interest_paid = table.Column<decimal>(type: "numeric", nullable: true),
                    insurance_paid = table.Column<decimal>(type: "numeric", nullable: true),
                    other_charges_paid = table.Column<decimal>(type: "numeric", nullable: true),
                    bank_transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_debt_payments", x => x.id);
                    table.CheckConstraint("ck_debt_payment_amount_positive", "amount > 0");
                    table.CheckConstraint("ck_debt_payment_cancelled_pair", "(cancelled_at IS NULL) OR cancelled_at IS NOT NULL");
                    table.ForeignKey(
                        name: "fk_financial_debt_payments_families_family_id",
                        column: x => x.family_id,
                        principalTable: "families",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_debt_payments_financial_accounts_family_id_financ",
                        columns: x => new { x.family_id, x.financial_account_id },
                        principalTable: "financial_accounts",
                        principalColumns: new[] { "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_debt_payments_financial_debt_installments_family_",
                        columns: x => new { x.family_id, x.installment_id },
                        principalTable: "financial_debt_installments",
                        principalColumns: new[] { "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_debt_payments_financial_transactions_family_id_ba",
                        columns: x => new { x.family_id, x.bank_transaction_id },
                        principalTable: "financial_transactions",
                        principalColumns: new[] { "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_financial_debt_installments_family_id_debt_id_installment_n",
                table: "financial_debt_installments",
                columns: new[] { "family_id", "debt_id", "installment_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_financial_debt_installments_family_id_due_date",
                table: "financial_debt_installments",
                columns: new[] { "family_id", "due_date" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_debt_payments_family_id_bank_transaction_id",
                table: "financial_debt_payments",
                columns: new[] { "family_id", "bank_transaction_id" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_debt_payments_family_id_financial_account_id",
                table: "financial_debt_payments",
                columns: new[] { "family_id", "financial_account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_debt_payments_family_id_installment_id",
                table: "financial_debt_payments",
                columns: new[] { "family_id", "installment_id" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_debt_payments_family_id_operation_id",
                table: "financial_debt_payments",
                columns: new[] { "family_id", "operation_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "financial_debt_payments");

            migrationBuilder.DropTable(
                name: "financial_debt_installments");
        }
    }
}
