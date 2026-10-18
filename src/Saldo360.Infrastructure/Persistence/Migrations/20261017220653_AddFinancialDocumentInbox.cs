using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFinancialDocumentInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "financial_document_inbox_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    external_source_id = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    original_file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    content_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    storage_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    parser = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    competence = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    credit_card_id = table.Column<Guid>(type: "uuid", nullable: true),
                    failure_code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_document_inbox_items", x => new { x.family_id, x.id });
                    table.ForeignKey(
                        name: "fk_financial_document_inbox_items_families_family_id",
                        column: x => x.family_id,
                        principalTable: "families",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_document_inbox_items_financial_credit_cards_famil",
                        columns: x => new { x.family_id, x.credit_card_id },
                        principalTable: "financial_credit_cards",
                        principalColumns: new[] { "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_financial_document_inbox_items_family_id_content_hash",
                table: "financial_document_inbox_items",
                columns: new[] { "family_id", "content_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_financial_document_inbox_items_family_id_credit_card_id",
                table: "financial_document_inbox_items",
                columns: new[] { "family_id", "credit_card_id" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_document_inbox_items_family_id_source_external_so",
                table: "financial_document_inbox_items",
                columns: new[] { "family_id", "source", "external_source_id" },
                unique: true,
                filter: "external_source_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_financial_document_inbox_items_family_id_status_received_at",
                table: "financial_document_inbox_items",
                columns: new[] { "family_id", "status", "received_at" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_document_inbox_items_storage_reference",
                table: "financial_document_inbox_items",
                column: "storage_reference",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "financial_document_inbox_items");
        }
    }
}
