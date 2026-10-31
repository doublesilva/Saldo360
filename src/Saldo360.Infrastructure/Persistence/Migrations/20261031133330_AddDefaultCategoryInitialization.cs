using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDefaultCategoryInitialization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "default_categories_catalog_version",
                table: "financial_workspaces",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "default_categories_initialized_at",
                table: "financial_workspaces",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_workspace_default_categories_initialization",
                table: "financial_workspaces",
                sql: "(default_categories_initialized_at IS NULL) = (default_categories_catalog_version IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_workspace_default_categories_initialization",
                table: "financial_workspaces");

            migrationBuilder.DropColumn(
                name: "default_categories_catalog_version",
                table: "financial_workspaces");

            migrationBuilder.DropColumn(
                name: "default_categories_initialized_at",
                table: "financial_workspaces");
        }
    }
}
