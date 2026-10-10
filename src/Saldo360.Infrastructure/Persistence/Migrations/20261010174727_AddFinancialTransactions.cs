using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFinancialTransactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "ak_financial_categories_family_id_id_type",
                table: "financial_categories",
                columns: new[] { "family_id", "id", "type" });

            migrationBuilder.AddUniqueConstraint(
                name: "ak_financial_accounts_family_id_id",
                table: "financial_accounts",
                columns: new[] { "family_id", "id" });

            migrationBuilder.CreateTable(
                name: "financial_transactions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    amount = table.Column<decimal>(type: "numeric", nullable: false),
                    transaction_date = table.Column<DateOnly>(type: "date", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_cancelled = table.Column<bool>(type: "boolean", nullable: false),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_transactions", x => x.id);
                    table.CheckConstraint("ck_financial_transactions_amount_positive", "amount > 0");
                    table.CheckConstraint("ck_financial_transactions_amount_range", "amount < 10000000000000");
                    table.CheckConstraint("ck_financial_transactions_amount_scale", "scale(amount) <= 2");
                    table.CheckConstraint("ck_financial_transactions_cancelled_at", "is_cancelled = (cancelled_at IS NOT NULL)");
                    table.CheckConstraint("ck_financial_transactions_cancelled_pair", "(cancelled_at IS NULL) = (cancelled_by_user_id IS NULL)");
                    table.CheckConstraint("ck_financial_transactions_currency", "currency = 'BRL'");
                    table.CheckConstraint("ck_financial_transactions_description_not_blank", "btrim(description) <> ''");
                    table.CheckConstraint("ck_financial_transactions_description_trimmed", "octet_length(description) = octet_length(btrim(description))");
                    table.CheckConstraint("ck_financial_transactions_type", "type IN ('Income', 'Expense')");
                    table.CheckConstraint("ck_financial_transactions_updated_pair", "(updated_at IS NULL) = (updated_by_user_id IS NULL)");
                    table.ForeignKey(
                        name: "fk_financial_transactions_accounts_same_family",
                        columns: x => new { x.family_id, x.account_id },
                        principalTable: "financial_accounts",
                        principalColumns: new[] { "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_transactions_application_users_cancelled_by_user_",
                        column: x => x.cancelled_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_transactions_application_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_transactions_application_users_updated_by_user_id",
                        column: x => x.updated_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_transactions_categories_same_family_type",
                        columns: x => new { x.family_id, x.category_id, x.type },
                        principalTable: "financial_categories",
                        principalColumns: new[] { "family_id", "id", "type" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_transactions_families_family_id",
                        column: x => x.family_id,
                        principalTable: "families",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_financial_transactions_cancelled_by_user_id",
                table: "financial_transactions",
                column: "cancelled_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_financial_transactions_created_by_user_id",
                table: "financial_transactions",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_financial_transactions_family_id_account_id",
                table: "financial_transactions",
                columns: new[] { "family_id", "account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_transactions_family_id_category_id_type",
                table: "financial_transactions",
                columns: new[] { "family_id", "category_id", "type" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_transactions_family_id_transaction_date_id",
                table: "financial_transactions",
                columns: new[] { "family_id", "transaction_date", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_financial_transactions_updated_by_user_id",
                table: "financial_transactions",
                column: "updated_by_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "financial_transactions");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_financial_categories_family_id_id_type",
                table: "financial_categories");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_financial_accounts_family_id_id",
                table: "financial_accounts");
        }
    }
}
