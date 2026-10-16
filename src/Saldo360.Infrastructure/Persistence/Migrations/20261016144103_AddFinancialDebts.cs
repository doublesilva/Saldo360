using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFinancialDebts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "financial_debts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    debt_type = table.Column<int>(type: "integer", nullable: false),
                    lender_name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    responsible_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    original_principal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    outstanding_principal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    outstanding_balance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    balance_reference_date = table.Column<DateOnly>(type: "date", nullable: true),
                    contract_date = table.Column<DateOnly>(type: "date", nullable: false),
                    first_due_date = table.Column<DateOnly>(type: "date", nullable: true),
                    installment_count = table.Column<int>(type: "integer", nullable: true),
                    paid_installment_count = table.Column<int>(type: "integer", nullable: true),
                    installment_amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    interest_rate = table.Column<decimal>(type: "numeric(9,4)", precision: 9, scale: 4, nullable: true),
                    interest_rate_period = table.Column<int>(type: "integer", nullable: true),
                    amortization_method = table.Column<int>(type: "integer", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_debts", x => x.id);
                    table.UniqueConstraint("ak_financial_debts_family_id_id", x => new { x.family_id, x.id });
                    table.CheckConstraint("ck_debts_archived", "(status = 3) = (archived_at IS NOT NULL)");
                    table.CheckConstraint("ck_debts_installments", "(installment_count IS NULL OR installment_count > 0) AND (paid_installment_count IS NULL OR paid_installment_count >= 0) AND (paid_installment_count IS NULL OR installment_count IS NULL OR paid_installment_count <= installment_count)");
                    table.CheckConstraint("ck_debts_nonnegative", "(outstanding_principal IS NULL OR outstanding_principal >= 0) AND (outstanding_balance IS NULL OR outstanding_balance >= 0) AND (installment_amount IS NULL OR installment_amount >= 0) AND (interest_rate IS NULL OR interest_rate >= 0)");
                    table.CheckConstraint("ck_debts_original_principal", "original_principal > 0");
                    table.CheckConstraint("ck_debts_reference", "(balance_reference_date IS NULL OR outstanding_balance IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_financial_debts_families_family_id",
                        column: x => x.family_id,
                        principalTable: "families",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_debts_financial_accounts_family_id_responsible_ac",
                        columns: x => new { x.family_id, x.responsible_account_id },
                        principalTable: "financial_accounts",
                        principalColumns: new[] { "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_financial_debts_family_id_responsible_account_id",
                table: "financial_debts",
                columns: new[] { "family_id", "responsible_account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_debts_family_id_status",
                table: "financial_debts",
                columns: new[] { "family_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "financial_debts");
        }
    }
}
