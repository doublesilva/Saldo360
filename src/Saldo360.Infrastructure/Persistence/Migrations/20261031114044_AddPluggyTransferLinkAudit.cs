using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPluggyTransferLinkAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "link_origin",
                table: "pluggy_postings",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "link_review_reason",
                table: "pluggy_postings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "link_rule_version",
                table: "pluggy_postings",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "pluggy_transfer_rejections",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    first_posting_id = table.Column<Guid>(type: "uuid", nullable: false),
                    second_posting_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rejected_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rejected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pluggy_transfer_rejections", x => x.id);
                    table.ForeignKey(
                        name: "fk_pluggy_transfer_rejections_application_users_rejected_by_us",
                        column: x => x.rejected_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pluggy_transfer_rejections_financial_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "financial_workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pluggy_transfer_rejections_pluggy_postings_first_posting_id",
                        column: x => x.first_posting_id,
                        principalTable: "pluggy_postings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pluggy_transfer_rejections_pluggy_postings_second_posting_id",
                        column: x => x.second_posting_id,
                        principalTable: "pluggy_postings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_pluggy_postings_link_origin",
                table: "pluggy_postings",
                sql: "link_origin IS NULL OR (link_origin IN ('Inferred','UserConfirmed') AND kind = 'Transfer' AND state = 'Applied')");

            migrationBuilder.CreateIndex(
                name: "ix_pluggy_transfer_rejections_rejected_by_user_id",
                table: "pluggy_transfer_rejections",
                column: "rejected_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_pluggy_transfer_rejections_second_posting_id",
                table: "pluggy_transfer_rejections",
                column: "second_posting_id");

            migrationBuilder.CreateIndex(
                name: "ix_pluggy_transfer_rejections_workspace_id",
                table: "pluggy_transfer_rejections",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "ux_pluggy_transfer_rejections_pair",
                table: "pluggy_transfer_rejections",
                columns: new[] { "first_posting_id", "second_posting_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pluggy_transfer_rejections");

            migrationBuilder.DropCheckConstraint(
                name: "ck_pluggy_postings_link_origin",
                table: "pluggy_postings");

            migrationBuilder.DropColumn(
                name: "link_origin",
                table: "pluggy_postings");

            migrationBuilder.DropColumn(
                name: "link_review_reason",
                table: "pluggy_postings");

            migrationBuilder.DropColumn(
                name: "link_rule_version",
                table: "pluggy_postings");
        }
    }
}
