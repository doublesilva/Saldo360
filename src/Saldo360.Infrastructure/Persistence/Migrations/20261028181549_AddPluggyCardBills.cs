using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPluggyCardBills : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pluggy_card_bills",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_link_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pluggy_bill_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    closing_date = table.Column<DateOnly>(type: "date", nullable: true),
                    due_date = table.Column<DateOnly>(type: "date", nullable: true),
                    total_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    currency = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    first_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pluggy_card_bills", x => x.id);
                    table.ForeignKey(
                        name: "fk_pluggy_card_bills_financial_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "financial_workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pluggy_card_bills_pluggy_account_links_account_link_id",
                        column: x => x.account_link_id,
                        principalTable: "pluggy_account_links",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_pluggy_card_bills_account_link_id_closing_date",
                table: "pluggy_card_bills",
                columns: new[] { "account_link_id", "closing_date" });

            migrationBuilder.CreateIndex(
                name: "ix_pluggy_card_bills_workspace_id",
                table: "pluggy_card_bills",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "ux_pluggy_card_bills_external",
                table: "pluggy_card_bills",
                columns: new[] { "account_link_id", "pluggy_bill_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pluggy_card_bills");
        }
    }
}
