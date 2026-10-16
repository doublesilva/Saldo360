using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDebtInstallmentReconciliations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "financial_debt_installment_reconciliations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    debt_installment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    planned_transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    removed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    removed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_debt_installment_reconciliations", x => x.id);
                    table.ForeignKey(
                        name: "fk_financial_debt_installment_reconciliations_families_family_",
                        column: x => x.family_id,
                        principalTable: "families",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_debt_installment_reconciliations_financial_debt_i",
                        columns: x => new { x.family_id, x.debt_installment_id },
                        principalTable: "financial_debt_installments",
                        principalColumns: new[] { "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_debt_installment_reconciliations_financial_planne",
                        columns: x => new { x.family_id, x.planned_transaction_id },
                        principalTable: "financial_planned_transactions",
                        principalColumns: new[] { "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_financial_debt_installment_reconciliations_family_id_debt_i",
                table: "financial_debt_installment_reconciliations",
                columns: new[] { "family_id", "debt_installment_id" },
                unique: true,
                filter: "removed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_financial_debt_installment_reconciliations_family_id_planne",
                table: "financial_debt_installment_reconciliations",
                columns: new[] { "family_id", "planned_transaction_id" },
                unique: true,
                filter: "removed_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "financial_debt_installment_reconciliations");
        }
    }
}
