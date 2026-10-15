using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCreditCardPurchases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "ak_financial_categories_family_id_id",
                table: "financial_categories",
                columns: new[] { "family_id", "id" });

            migrationBuilder.CreateTable(
                name: "financial_credit_card_purchases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    credit_card_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: false),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    purchase_date = table.Column<DateOnly>(type: "date", nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric", nullable: false),
                    installment_count = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_credit_card_purchases", x => x.id);
                    table.UniqueConstraint("ak_financial_credit_card_purchases_family_id_id", x => new { x.family_id, x.id });
                    table.CheckConstraint("ck_credit_card_purchases_description_not_blank", "btrim(description) <> ''");
                    table.CheckConstraint("ck_credit_card_purchases_installment_count", "installment_count BETWEEN 1 AND 120");
                    table.CheckConstraint("ck_credit_card_purchases_status", "status IN ('Active', 'Cancelled')");
                    table.CheckConstraint("ck_credit_card_purchases_total_positive", "total_amount > 0");
                    table.CheckConstraint("ck_credit_card_purchases_total_scale", "scale(total_amount) <= 2");
                    table.ForeignKey(
                        name: "fk_credit_card_purchases_cards_same_family",
                        columns: x => new { x.family_id, x.credit_card_id },
                        principalTable: "financial_credit_cards",
                        principalColumns: new[] { "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_credit_card_purchases_categories_same_family",
                        columns: x => new { x.family_id, x.category_id },
                        principalTable: "financial_categories",
                        principalColumns: new[] { "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_credit_card_purchases_application_users_cancelled",
                        column: x => x.cancelled_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_credit_card_purchases_application_users_created_b",
                        column: x => x.created_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_credit_card_purchases_application_users_updated_b",
                        column: x => x.updated_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_credit_card_purchases_families_family_id",
                        column: x => x.family_id,
                        principalTable: "families",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "financial_credit_card_installments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purchase_id = table.Column<Guid>(type: "uuid", nullable: false),
                    installment_number = table.Column<int>(type: "integer", nullable: false),
                    amount = table.Column<decimal>(type: "numeric", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_credit_card_installments", x => x.id);
                    table.CheckConstraint("ck_credit_card_installments_amount_positive", "amount > 0");
                    table.CheckConstraint("ck_credit_card_installments_amount_scale", "scale(amount) <= 2");
                    table.CheckConstraint("ck_credit_card_installments_number_positive", "installment_number BETWEEN 1 AND 120");
                    table.CheckConstraint("ck_credit_card_installments_status", "status IN ('Active', 'Cancelled')");
                    table.ForeignKey(
                        name: "fk_credit_card_installments_purchases_same_family",
                        columns: x => new { x.family_id, x.purchase_id },
                        principalTable: "financial_credit_card_purchases",
                        principalColumns: new[] { "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_card_installments_family_id_purchase_id_in",
                table: "financial_credit_card_installments",
                columns: new[] { "family_id", "purchase_id", "installment_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_card_purchases_cancelled_by_user_id",
                table: "financial_credit_card_purchases",
                column: "cancelled_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_card_purchases_created_by_user_id",
                table: "financial_credit_card_purchases",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_card_purchases_family_id_category_id",
                table: "financial_credit_card_purchases",
                columns: new[] { "family_id", "category_id" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_card_purchases_family_id_credit_card_id_pu",
                table: "financial_credit_card_purchases",
                columns: new[] { "family_id", "credit_card_id", "purchase_date" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_card_purchases_updated_by_user_id",
                table: "financial_credit_card_purchases",
                column: "updated_by_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "financial_credit_card_installments");

            migrationBuilder.DropTable(
                name: "financial_credit_card_purchases");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_financial_categories_family_id_id",
                table: "financial_categories");
        }
    }
}
