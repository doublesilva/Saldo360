using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCreditCardImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "financial_credit_card_import_batches",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    credit_card_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payload_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_credit_card_import_batches", x => x.id);
                    table.UniqueConstraint("ak_financial_credit_card_import_batches_family_id_id", x => new { x.family_id, x.id });
                    table.CheckConstraint("ck_credit_card_import_batches_status", "status IN ('Completed')");
                    table.ForeignKey(
                        name: "fk_financial_credit_card_import_batches_application_users_crea",
                        column: x => x.created_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_credit_card_import_batches_families_family_id",
                        column: x => x.family_id,
                        principalTable: "families",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_credit_card_import_batches_financial_credit_cards",
                        columns: x => new { x.family_id, x.credit_card_id },
                        principalTable: "financial_credit_cards",
                        principalColumns: new[] { "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "financial_credit_card_import_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    credit_card_id = table.Column<Guid>(type: "uuid", nullable: false),
                    import_batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    row_number = table.Column<int>(type: "integer", nullable: false),
                    external_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    purchase_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_credit_card_import_items", x => x.id);
                    table.CheckConstraint("ck_credit_card_import_items_status", "status IN ('Imported')");
                    table.ForeignKey(
                        name: "fk_financial_credit_card_import_items_financial_credit_card_im",
                        columns: x => new { x.family_id, x.import_batch_id },
                        principalTable: "financial_credit_card_import_batches",
                        principalColumns: new[] { "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_credit_card_import_items_financial_credit_card_pu",
                        columns: x => new { x.family_id, x.purchase_id },
                        principalTable: "financial_credit_card_purchases",
                        principalColumns: new[] { "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_credit_card_import_items_financial_credit_cards_f",
                        columns: x => new { x.family_id, x.credit_card_id },
                        principalTable: "financial_credit_cards",
                        principalColumns: new[] { "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_card_import_batches_created_by_user_id",
                table: "financial_credit_card_import_batches",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_card_import_batches_family_id_credit_card_",
                table: "financial_credit_card_import_batches",
                columns: new[] { "family_id", "credit_card_id" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_card_import_batches_payload_hash",
                table: "financial_credit_card_import_batches",
                column: "payload_hash");

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_card_import_items_family_id_credit_card_id",
                table: "financial_credit_card_import_items",
                columns: new[] { "family_id", "credit_card_id", "external_id" },
                unique: true,
                filter: "external_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_card_import_items_family_id_import_batch_id",
                table: "financial_credit_card_import_items",
                columns: new[] { "family_id", "import_batch_id" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_card_import_items_family_id_purchase_id",
                table: "financial_credit_card_import_items",
                columns: new[] { "family_id", "purchase_id" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_credit_card_import_items_import_batch_id_row_numb",
                table: "financial_credit_card_import_items",
                columns: new[] { "import_batch_id", "row_number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "financial_credit_card_import_items");

            migrationBuilder.DropTable(
                name: "financial_credit_card_import_batches");
        }
    }
}
