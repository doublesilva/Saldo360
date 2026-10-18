using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFinancialDocumentClassification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "classification_confidence",
                table: "financial_document_inbox_items",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "classification_failure_code",
                table: "financial_document_inbox_items",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "classification_source",
                table: "financial_document_inbox_items",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "classified_at",
                table: "financial_document_inbox_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "document_type",
                table: "financial_document_inbox_items",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "classification_confidence",
                table: "financial_document_inbox_items");

            migrationBuilder.DropColumn(
                name: "classification_failure_code",
                table: "financial_document_inbox_items");

            migrationBuilder.DropColumn(
                name: "classification_source",
                table: "financial_document_inbox_items");

            migrationBuilder.DropColumn(
                name: "classified_at",
                table: "financial_document_inbox_items");

            migrationBuilder.DropColumn(
                name: "document_type",
                table: "financial_document_inbox_items");
        }
    }
}
