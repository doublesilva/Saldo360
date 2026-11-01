using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPluggyDetectedCardRecurrences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "merchant_category",
                table: "pluggy_imported_transactions",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "merchant_name",
                table: "pluggy_imported_transactions",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "detection_confidence",
                table: "financial_recurrences",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "evidence_count",
                table: "financial_recurrences",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "external_key",
                table: "financial_recurrences",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_user_adjusted",
                table: "financial_recurrences",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "origin",
                table: "financial_recurrences",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Manual");

            migrationBuilder.CreateIndex(
                name: "ix_financial_recurrences_workspace_id_external_key",
                table: "financial_recurrences",
                columns: new[] { "workspace_id", "external_key" },
                unique: true,
                filter: "external_key IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_financial_recurrences_workspace_id_external_key",
                table: "financial_recurrences");

            migrationBuilder.DropColumn(
                name: "merchant_category",
                table: "pluggy_imported_transactions");

            migrationBuilder.DropColumn(
                name: "merchant_name",
                table: "pluggy_imported_transactions");

            migrationBuilder.DropColumn(
                name: "detection_confidence",
                table: "financial_recurrences");

            migrationBuilder.DropColumn(
                name: "evidence_count",
                table: "financial_recurrences");

            migrationBuilder.DropColumn(
                name: "external_key",
                table: "financial_recurrences");

            migrationBuilder.DropColumn(
                name: "is_user_adjusted",
                table: "financial_recurrences");

            migrationBuilder.DropColumn(
                name: "origin",
                table: "financial_recurrences");
        }
    }
}
