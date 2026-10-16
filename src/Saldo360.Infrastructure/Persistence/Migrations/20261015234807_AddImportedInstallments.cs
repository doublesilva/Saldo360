using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddImportedInstallments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "ak_financial_credit_card_installments_family_id_id",
                table: "financial_credit_card_installments",
                columns: new[] { "family_id", "id" });

            migrationBuilder.CreateTable(
                name: "financial_credit_card_imported_installments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    credit_card_id = table.Column<Guid>(type: "uuid", nullable: false),
                    import_id = table.Column<Guid>(type: "uuid", nullable: false),
                    row_number = table.Column<int>(type: "integer", nullable: false),
                    external_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    linked_installment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transaction_date = table.Column<DateOnly>(type: "date", nullable: false),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    amount = table.Column<decimal>(type: "numeric", nullable: false),
                    installment_number = table.Column<int>(type: "integer", nullable: false),
                    installment_count = table.Column<int>(type: "integer", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_historical = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_credit_card_imported_installments", x => x.id);
                    table.CheckConstraint("ck_imported_installments_amount_positive", "amount > 0");
                    table.CheckConstraint("ck_imported_installments_amount_scale", "scale(amount) <= 2");
                    table.CheckConstraint("ck_imported_installments_mode", "(is_historical = true AND linked_installment_id IS NULL) OR (is_historical = false AND linked_installment_id IS NOT NULL)");
                    table.CheckConstraint("ck_imported_installments_number", "installment_number >= 1 AND installment_number <= installment_count");
                    table.CheckConstraint("ck_imported_installments_status", "status IN ('Active', 'Cancelled')");
                    table.ForeignKey(
                        name: "fk_financial_credit_card_imported_installments_application_use",
                        column: x => x.cancelled_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_credit_card_imported_installments_application_use1",
                        column: x => x.created_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_credit_card_imported_installments_families_family",
                        column: x => x.family_id,
                        principalTable: "families",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_credit_card_imported_installments_financial_categ",
                        columns: x => new { x.family_id, x.category_id },
                        principalTable: "financial_categories",
                        principalColumns: new[] { "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_credit_card_imported_installments_financial_credi",
                        columns: x => new { x.family_id, x.credit_card_id },
                        principalTable: "financial_credit_cards",
                        principalColumns: new[] { "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_credit_card_imported_installments_financial_credi1",
                        columns: x => new { x.family_id, x.invoice_id },
                        principalTable: "financial_credit_card_invoices",
                        principalColumns: new[] { "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_credit_card_imported_installments_financial_credi2",
                        columns: x => new { x.family_id, x.linked_installment_id },
                        principalTable: "financial_credit_card_installments",
                        principalColumns: new[] { "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_card_imported_installments_cancelled_by_us",
                table: "financial_credit_card_imported_installments",
                column: "cancelled_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_card_imported_installments_created_by_user",
                table: "financial_credit_card_imported_installments",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_card_imported_installments_family_id_categ",
                table: "financial_credit_card_imported_installments",
                columns: new[] { "family_id", "category_id" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_card_imported_installments_family_id_credi",
                table: "financial_credit_card_imported_installments",
                columns: new[] { "family_id", "credit_card_id", "external_id" },
                unique: true,
                filter: "external_id IS NOT NULL AND status = 'Active'");

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_card_imported_installments_family_id_credi1",
                table: "financial_credit_card_imported_installments",
                columns: new[] { "family_id", "credit_card_id", "import_id", "row_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_card_imported_installments_family_id_invoi",
                table: "financial_credit_card_imported_installments",
                columns: new[] { "family_id", "invoice_id" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_card_imported_installments_family_id_linke",
                table: "financial_credit_card_imported_installments",
                columns: new[] { "family_id", "linked_installment_id" },
                unique: true,
                filter: "linked_installment_id IS NOT NULL AND status = 'Active'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "financial_credit_card_imported_installments");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_financial_credit_card_installments_family_id_id",
                table: "financial_credit_card_installments");
        }
    }
}
