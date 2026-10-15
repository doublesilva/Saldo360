using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFinancialCreditCards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "ak_family_members_family_id_user_id",
                table: "family_members",
                columns: new[] { "family_id", "user_id" });

            migrationBuilder.CreateTable(
                name: "financial_credit_cards",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    issuer = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    holder_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    credit_limit = table.Column<decimal>(type: "numeric", nullable: false),
                    closing_day = table.Column<int>(type: "integer", nullable: false),
                    due_day = table.Column<int>(type: "integer", nullable: false),
                    default_payment_account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    archived_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_credit_cards", x => x.id);
                    table.UniqueConstraint("ak_financial_credit_cards_family_id_id", x => new { x.family_id, x.id });
                    table.CheckConstraint("ck_financial_credit_cards_archived", "is_archived = (archived_at IS NOT NULL) AND (archived_at IS NULL) = (archived_by_user_id IS NULL)");
                    table.CheckConstraint("ck_financial_credit_cards_closing_day", "closing_day BETWEEN 1 AND 31");
                    table.CheckConstraint("ck_financial_credit_cards_credit_limit_non_negative", "credit_limit >= 0");
                    table.CheckConstraint("ck_financial_credit_cards_credit_limit_scale", "scale(credit_limit) <= 2");
                    table.CheckConstraint("ck_financial_credit_cards_due_day", "due_day BETWEEN 1 AND 31");
                    table.CheckConstraint("ck_financial_credit_cards_issuer_not_blank", "btrim(issuer) <> ''");
                    table.CheckConstraint("ck_financial_credit_cards_name_not_blank", "btrim(name) <> ''");
                    table.ForeignKey(
                        name: "fk_financial_credit_cards_application_users_archived_by_user_id",
                        column: x => x.archived_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_credit_cards_application_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_credit_cards_application_users_updated_by_user_id",
                        column: x => x.updated_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_credit_cards_families_family_id",
                        column: x => x.family_id,
                        principalTable: "families",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_credit_cards_family_accounts",
                        columns: x => new { x.family_id, x.default_payment_account_id },
                        principalTable: "financial_accounts",
                        principalColumns: new[] { "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_credit_cards_family_members_holder",
                        columns: x => new { x.family_id, x.holder_user_id },
                        principalTable: "family_members",
                        principalColumns: new[] { "family_id", "user_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_cards_archived_by_user_id",
                table: "financial_credit_cards",
                column: "archived_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_cards_created_by_user_id",
                table: "financial_credit_cards",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_cards_family_id_default_payment_account_id",
                table: "financial_credit_cards",
                columns: new[] { "family_id", "default_payment_account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_cards_family_id_holder_user_id",
                table: "financial_credit_cards",
                columns: new[] { "family_id", "holder_user_id" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_cards_family_id_is_archived",
                table: "financial_credit_cards",
                columns: new[] { "family_id", "is_archived" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_cards_updated_by_user_id",
                table: "financial_credit_cards",
                column: "updated_by_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "financial_credit_cards");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_family_members_family_id_user_id",
                table: "family_members");
        }
    }
}
