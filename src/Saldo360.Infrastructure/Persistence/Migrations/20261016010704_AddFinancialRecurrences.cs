using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFinancialRecurrences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "financial_recurrences",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    financial_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    financial_category_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    amount = table.Column<decimal>(type: "numeric", nullable: false),
                    is_estimated = table.Column<bool>(type: "boolean", nullable: false),
                    frequency = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    interval_value = table.Column<int>(type: "integer", nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: true),
                    occurrence_count = table.Column<int>(type: "integer", nullable: true),
                    day_of_month = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_recurrences", x => x.id);
                    table.CheckConstraint("ck_recurrences_amount_positive", "amount > 0");
                    table.CheckConstraint("ck_recurrences_amount_scale", "scale(amount) <= 2");
                    table.CheckConstraint("ck_recurrences_count", "occurrence_count IS NULL OR occurrence_count > 0");
                    table.CheckConstraint("ck_recurrences_day", "day_of_month BETWEEN 1 AND 31");
                    table.CheckConstraint("ck_recurrences_end", "end_date IS NULL OR end_date >= start_date");
                    table.CheckConstraint("ck_recurrences_end_mode", "NOT (end_date IS NOT NULL AND occurrence_count IS NOT NULL)");
                    table.CheckConstraint("ck_recurrences_interval", "interval_value BETWEEN 1 AND 120");
                    table.ForeignKey(
                        name: "fk_financial_recurrences_families_family_id",
                        column: x => x.family_id,
                        principalTable: "families",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_recurrences_financial_accounts_family_id_financia",
                        columns: x => new { x.family_id, x.financial_account_id },
                        principalTable: "financial_accounts",
                        principalColumns: new[] { "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_recurrences_financial_categories_family_id_financ",
                        columns: x => new { x.family_id, x.financial_category_id, x.type },
                        principalTable: "financial_categories",
                        principalColumns: new[] { "family_id", "id", "type" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_financial_recurrences_family_id_financial_account_id",
                table: "financial_recurrences",
                columns: new[] { "family_id", "financial_account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_recurrences_family_id_financial_category_id_type",
                table: "financial_recurrences",
                columns: new[] { "family_id", "financial_category_id", "type" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_recurrences_family_id_status_start_date",
                table: "financial_recurrences",
                columns: new[] { "family_id", "status", "start_date" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "financial_recurrences");
        }
    }
}
