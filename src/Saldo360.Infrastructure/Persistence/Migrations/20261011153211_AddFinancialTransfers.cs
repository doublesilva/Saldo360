using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFinancialTransfers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "financial_transfers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    destination_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric", nullable: false),
                    transfer_date = table.Column<DateOnly>(type: "date", nullable: false),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_cancelled = table.Column<bool>(type: "boolean", nullable: false),
                    cancelled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_transfers", x => x.id);
                    table.CheckConstraint("ck_financial_transfers_amount_positive", "amount > 0");
                    table.CheckConstraint("ck_financial_transfers_amount_range", "amount < 10000000000000");
                    table.CheckConstraint("ck_financial_transfers_amount_scale", "scale(amount) <= 2");
                    table.CheckConstraint("ck_financial_transfers_cancelled_at", "is_cancelled = (cancelled_at IS NOT NULL)");
                    table.CheckConstraint("ck_financial_transfers_cancelled_pair", "(cancelled_at IS NULL) = (cancelled_by_user_id IS NULL)");
                    table.CheckConstraint("ck_financial_transfers_currency", "currency = 'BRL'");
                    table.CheckConstraint("ck_financial_transfers_description_not_blank", "btrim(description) <> ''");
                    table.CheckConstraint("ck_financial_transfers_description_trimmed", "octet_length(description) = octet_length(btrim(description))");
                    table.CheckConstraint("ck_financial_transfers_distinct_accounts", "source_account_id <> destination_account_id");
                    table.ForeignKey(
                        name: "fk_financial_transfers_application_users_cancelled_by_user_id",
                        column: x => x.cancelled_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_transfers_application_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_transfers_destination_account_same_family",
                        columns: x => new { x.family_id, x.destination_account_id },
                        principalTable: "financial_accounts",
                        principalColumns: new[] { "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_transfers_families_family_id",
                        column: x => x.family_id,
                        principalTable: "families",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_transfers_source_account_same_family",
                        columns: x => new { x.family_id, x.source_account_id },
                        principalTable: "financial_accounts",
                        principalColumns: new[] { "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_financial_transfers_cancelled_by_user_id",
                table: "financial_transfers",
                column: "cancelled_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_financial_transfers_created_by_user_id",
                table: "financial_transfers",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_financial_transfers_family_id_destination_account_id",
                table: "financial_transfers",
                columns: new[] { "family_id", "destination_account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_transfers_family_id_source_account_id",
                table: "financial_transfers",
                columns: new[] { "family_id", "source_account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_transfers_family_id_transfer_date_id",
                table: "financial_transfers",
                columns: new[] { "family_id", "transfer_date", "id" },
                descending: new[] { false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "financial_transfers");
        }
    }
}
