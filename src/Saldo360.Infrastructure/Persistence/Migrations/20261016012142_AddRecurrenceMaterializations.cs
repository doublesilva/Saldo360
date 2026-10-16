using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRecurrenceMaterializations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "ak_financial_recurrences_family_id_id",
                table: "financial_recurrences",
                columns: new[] { "family_id", "id" });

            migrationBuilder.CreateTable(
                name: "financial_recurrence_materializations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recurrence_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurrence_number = table.Column<int>(type: "integer", nullable: false),
                    scheduled_date = table.Column<DateOnly>(type: "date", nullable: false),
                    planned_transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_recurrence_materializations", x => x.id);
                    table.ForeignKey(
                        name: "fk_financial_recurrence_materializations_families_family_id",
                        column: x => x.family_id,
                        principalTable: "families",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_recurrence_materializations_financial_planned_tra",
                        column: x => x.planned_transaction_id,
                        principalTable: "financial_planned_transactions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_recurrence_materializations_financial_recurrences",
                        columns: x => new { x.family_id, x.recurrence_id },
                        principalTable: "financial_recurrences",
                        principalColumns: new[] { "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_financial_recurrence_materializations_family_id_recurrence_",
                table: "financial_recurrence_materializations",
                columns: new[] { "family_id", "recurrence_id", "occurrence_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_financial_recurrence_materializations_family_id_recurrence_1",
                table: "financial_recurrence_materializations",
                columns: new[] { "family_id", "recurrence_id", "scheduled_date" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_recurrence_materializations_planned_transaction_id",
                table: "financial_recurrence_materializations",
                column: "planned_transaction_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "financial_recurrence_materializations");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_financial_recurrences_family_id_id",
                table: "financial_recurrences");
        }
    }
}
