using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPluggyCategoryClassification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_pluggy_postings_category_source",
                table: "pluggy_postings");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "category_classified_at",
                table: "pluggy_postings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "category_method_version",
                table: "pluggy_postings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "category_needs_review",
                table: "pluggy_postings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "category_reason",
                table: "pluggy_postings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_pluggy_postings_category_source",
                table: "pluggy_postings",
                sql: "category_source IN ('None','Mapped','User','Pluggy','Rule','AI')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_pluggy_postings_category_source",
                table: "pluggy_postings");

            migrationBuilder.DropColumn(
                name: "category_classified_at",
                table: "pluggy_postings");

            migrationBuilder.DropColumn(
                name: "category_method_version",
                table: "pluggy_postings");

            migrationBuilder.DropColumn(
                name: "category_needs_review",
                table: "pluggy_postings");

            migrationBuilder.DropColumn(
                name: "category_reason",
                table: "pluggy_postings");

            migrationBuilder.AddCheckConstraint(
                name: "ck_pluggy_postings_category_source",
                table: "pluggy_postings",
                sql: "category_source IN ('None','Mapped','User')");
        }
    }
}
