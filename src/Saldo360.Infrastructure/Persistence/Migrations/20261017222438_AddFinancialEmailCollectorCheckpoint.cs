using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFinancialEmailCollectorCheckpoint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "financial_email_collector_checkpoints",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    profile_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    last_message_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_messages_scanned = table.Column<int>(type: "integer", nullable: false),
                    last_accepted_documents = table.Column<int>(type: "integer", nullable: false),
                    last_failures = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_email_collector_checkpoints", x => new { x.family_id, x.id });
                    table.ForeignKey(
                        name: "fk_financial_email_collector_checkpoints_families_family_id",
                        column: x => x.family_id,
                        principalTable: "families",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_financial_email_collector_checkpoints_family_id_profile_name",
                table: "financial_email_collector_checkpoints",
                columns: new[] { "family_id", "profile_name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "financial_email_collector_checkpoints");
        }
    }
}
