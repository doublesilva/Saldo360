using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFinancialEmailCollectorOperationalStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "last_candidate_attachments",
                table: "financial_email_collector_checkpoints",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "last_duplicates",
                table: "financial_email_collector_checkpoints",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "last_duration_milliseconds",
                table: "financial_email_collector_checkpoints",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "last_needs_review",
                table: "financial_email_collector_checkpoints",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "last_result",
                table: "financial_email_collector_checkpoints",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_successful_run_at",
                table: "financial_email_collector_checkpoints",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_candidate_attachments",
                table: "financial_email_collector_checkpoints");

            migrationBuilder.DropColumn(
                name: "last_duplicates",
                table: "financial_email_collector_checkpoints");

            migrationBuilder.DropColumn(
                name: "last_duration_milliseconds",
                table: "financial_email_collector_checkpoints");

            migrationBuilder.DropColumn(
                name: "last_needs_review",
                table: "financial_email_collector_checkpoints");

            migrationBuilder.DropColumn(
                name: "last_result",
                table: "financial_email_collector_checkpoints");

            migrationBuilder.DropColumn(
                name: "last_successful_run_at",
                table: "financial_email_collector_checkpoints");
        }
    }
}
