using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOverdraftRecoveryTargetFixation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "fixed_at",
                table: "overdraft_recovery_snapshots",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "target_capacity",
                table: "overdraft_recovery_snapshots",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "target_factor",
                table: "overdraft_recovery_snapshots",
                type: "numeric",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "fixed_at",
                table: "overdraft_recovery_snapshots");

            migrationBuilder.DropColumn(
                name: "target_capacity",
                table: "overdraft_recovery_snapshots");

            migrationBuilder.DropColumn(
                name: "target_factor",
                table: "overdraft_recovery_snapshots");
        }
    }
}
