using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceAccountBindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "service_account_bindings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recurring_template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    issuer_normalized = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    account_reference_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    account_reference_masked = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    service_reference_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    service_reference_masked = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    origin = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_service_account_bindings", x => x.id);
                    table.CheckConstraint("ck_service_account_bindings_reference", "account_reference_hash IS NOT NULL OR service_reference_hash IS NOT NULL");
                    table.ForeignKey(
                        name: "fk_service_account_bindings_families_family_id",
                        column: x => x.family_id,
                        principalTable: "families",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_service_account_bindings_financial_recurrences_family_id_re",
                        columns: x => new { x.family_id, x.recurring_template_id },
                        principalTable: "financial_recurrences",
                        principalColumns: new[] { "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_service_account_bindings_family_id_document_type_account_re",
                table: "service_account_bindings",
                columns: new[] { "family_id", "document_type", "account_reference_hash" },
                unique: true,
                filter: "account_reference_hash IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_service_account_bindings_family_id_document_type_service_re",
                table: "service_account_bindings",
                columns: new[] { "family_id", "document_type", "service_reference_hash" },
                unique: true,
                filter: "service_reference_hash IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_service_account_bindings_family_id_recurring_template_id",
                table: "service_account_bindings",
                columns: new[] { "family_id", "recurring_template_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "service_account_bindings");
        }
    }
}
