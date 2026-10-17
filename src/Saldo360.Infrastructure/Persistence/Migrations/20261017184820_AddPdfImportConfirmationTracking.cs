using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPdfImportConfirmationTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_credit_card_import_items_status",
                table: "financial_credit_card_import_items");

            migrationBuilder.DropIndex(
                name: "ix_financial_credit_card_import_batches_family_id_credit_card_",
                table: "financial_credit_card_import_batches");

            migrationBuilder.AlterColumn<Guid>(
                name: "purchase_id",
                table: "financial_credit_card_import_items",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "component_id",
                table: "financial_credit_card_import_items",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "component_type",
                table: "financial_credit_card_import_items",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "entity_id",
                table: "financial_credit_card_import_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "competence",
                table: "financial_credit_card_import_batches",
                type: "character varying(7)",
                maxLength: 7,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "decision_hash",
                table: "financial_credit_card_import_batches",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "document_hash",
                table: "financial_credit_card_import_batches",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "official_total",
                table: "financial_credit_card_import_batches",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "parser",
                table: "financial_credit_card_import_batches",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source",
                table: "financial_credit_card_import_batches",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "Csv");

            migrationBuilder.AddCheckConstraint(
                name: "ck_credit_card_import_items_status",
                table: "financial_credit_card_import_items",
                sql: "status IN ('Imported', 'AlreadyExisting', 'Informational')");

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_card_import_batches_family_id_credit_card_",
                table: "financial_credit_card_import_batches",
                columns: new[] { "family_id", "credit_card_id", "source", "document_hash" },
                unique: true,
                filter: "document_hash IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_credit_card_import_batches_source",
                table: "financial_credit_card_import_batches",
                sql: "source IN ('Csv', 'Pdf')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM financial_credit_card_import_items WHERE import_batch_id IN (SELECT id FROM financial_credit_card_import_batches WHERE source = 'Pdf')");
            migrationBuilder.Sql("DELETE FROM financial_credit_card_import_batches WHERE source = 'Pdf'");
            migrationBuilder.DropCheckConstraint(
                name: "ck_credit_card_import_items_status",
                table: "financial_credit_card_import_items");

            migrationBuilder.DropIndex(
                name: "ix_financial_credit_card_import_batches_family_id_credit_card_",
                table: "financial_credit_card_import_batches");

            migrationBuilder.DropCheckConstraint(
                name: "ck_credit_card_import_batches_source",
                table: "financial_credit_card_import_batches");

            migrationBuilder.DropColumn(
                name: "component_id",
                table: "financial_credit_card_import_items");

            migrationBuilder.DropColumn(
                name: "component_type",
                table: "financial_credit_card_import_items");

            migrationBuilder.DropColumn(
                name: "entity_id",
                table: "financial_credit_card_import_items");

            migrationBuilder.DropColumn(
                name: "competence",
                table: "financial_credit_card_import_batches");

            migrationBuilder.DropColumn(
                name: "decision_hash",
                table: "financial_credit_card_import_batches");

            migrationBuilder.DropColumn(
                name: "document_hash",
                table: "financial_credit_card_import_batches");

            migrationBuilder.DropColumn(
                name: "official_total",
                table: "financial_credit_card_import_batches");

            migrationBuilder.DropColumn(
                name: "parser",
                table: "financial_credit_card_import_batches");

            migrationBuilder.DropColumn(
                name: "source",
                table: "financial_credit_card_import_batches");

            migrationBuilder.AlterColumn<Guid>(
                name: "purchase_id",
                table: "financial_credit_card_import_items",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_credit_card_import_items_status",
                table: "financial_credit_card_import_items",
                sql: "status IN ('Imported')");

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_card_import_batches_family_id_credit_card_",
                table: "financial_credit_card_import_batches",
                columns: new[] { "family_id", "credit_card_id" });
        }
    }
}
