using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceBillOccurrenceConfirmations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "service_bill_competence",
                table: "financial_document_inbox_items",
                type: "character varying(7)",
                maxLength: 7,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "service_bill_confirmed_amount",
                table: "financial_document_inbox_items",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "service_bill_confirmed_at",
                table: "financial_document_inbox_items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "service_bill_occurrence_id",
                table: "financial_document_inbox_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "service_bill_projected_amount",
                table: "financial_document_inbox_items",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "service_bill_recurrence_id",
                table: "financial_document_inbox_items",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_financial_document_inbox_items_family_id_service_bill_occur",
                table: "financial_document_inbox_items",
                columns: new[] { "family_id", "service_bill_occurrence_id" },
                unique: true,
                filter: "service_bill_occurrence_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_financial_document_inbox_items_family_id_service_bill_recur",
                table: "financial_document_inbox_items",
                columns: new[] { "family_id", "service_bill_recurrence_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_financial_document_inbox_service_bill_amount_scale",
                table: "financial_document_inbox_items",
                sql: "service_bill_projected_amount IS NULL OR (scale(service_bill_projected_amount) <= 2 AND scale(service_bill_confirmed_amount) <= 2)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_financial_document_inbox_service_bill_confirmation_complete",
                table: "financial_document_inbox_items",
                sql: "(service_bill_occurrence_id IS NULL AND service_bill_recurrence_id IS NULL AND service_bill_competence IS NULL AND service_bill_projected_amount IS NULL AND service_bill_confirmed_amount IS NULL AND service_bill_confirmed_at IS NULL) OR (service_bill_occurrence_id IS NOT NULL AND service_bill_recurrence_id IS NOT NULL AND service_bill_competence IS NOT NULL AND service_bill_projected_amount > 0 AND service_bill_confirmed_amount > 0 AND service_bill_confirmed_at IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "fk_financial_document_inbox_items_financial_planned_transactio",
                table: "financial_document_inbox_items",
                columns: new[] { "family_id", "service_bill_occurrence_id" },
                principalTable: "financial_planned_transactions",
                principalColumns: new[] { "family_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_financial_document_inbox_items_financial_recurrences_family",
                table: "financial_document_inbox_items",
                columns: new[] { "family_id", "service_bill_recurrence_id" },
                principalTable: "financial_recurrences",
                principalColumns: new[] { "family_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_financial_document_inbox_items_financial_planned_transactio",
                table: "financial_document_inbox_items");

            migrationBuilder.DropForeignKey(
                name: "fk_financial_document_inbox_items_financial_recurrences_family",
                table: "financial_document_inbox_items");

            migrationBuilder.DropIndex(
                name: "ix_financial_document_inbox_items_family_id_service_bill_occur",
                table: "financial_document_inbox_items");

            migrationBuilder.DropIndex(
                name: "ix_financial_document_inbox_items_family_id_service_bill_recur",
                table: "financial_document_inbox_items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_financial_document_inbox_service_bill_amount_scale",
                table: "financial_document_inbox_items");

            migrationBuilder.DropCheckConstraint(
                name: "ck_financial_document_inbox_service_bill_confirmation_complete",
                table: "financial_document_inbox_items");

            migrationBuilder.DropColumn(
                name: "service_bill_competence",
                table: "financial_document_inbox_items");

            migrationBuilder.DropColumn(
                name: "service_bill_confirmed_amount",
                table: "financial_document_inbox_items");

            migrationBuilder.DropColumn(
                name: "service_bill_confirmed_at",
                table: "financial_document_inbox_items");

            migrationBuilder.DropColumn(
                name: "service_bill_occurrence_id",
                table: "financial_document_inbox_items");

            migrationBuilder.DropColumn(
                name: "service_bill_projected_amount",
                table: "financial_document_inbox_items");

            migrationBuilder.DropColumn(
                name: "service_bill_recurrence_id",
                table: "financial_document_inbox_items");
        }
    }
}
