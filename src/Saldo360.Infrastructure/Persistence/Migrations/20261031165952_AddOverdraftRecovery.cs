using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOverdraftRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "overdraft_limit",
                table: "financial_accounts",
                type: "numeric",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "overdraft_recovery_snapshots",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    competence = table.Column<DateOnly>(type: "date", nullable: false),
                    opening_balance = table.Column<decimal>(type: "numeric", nullable: false),
                    reference_date = table.Column<DateOnly>(type: "date", nullable: false),
                    planned_income = table.Column<decimal>(type: "numeric", nullable: false),
                    planned_expense = table.Column<decimal>(type: "numeric", nullable: false),
                    planned_closing_balance = table.Column<decimal>(type: "numeric", nullable: false),
                    recovery_target = table.Column<decimal>(type: "numeric", nullable: false),
                    captured_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    quality = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    composition = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_overdraft_recovery_snapshots", x => x.id);
                    table.CheckConstraint("ck_overdraft_recovery_snapshot_competence", "extract(day from competence) = 1");
                    table.CheckConstraint("ck_overdraft_recovery_snapshot_money", "abs(opening_balance) < 10000000000000 AND abs(planned_income) < 10000000000000 AND abs(planned_expense) < 10000000000000 AND abs(planned_closing_balance) < 10000000000000 AND abs(recovery_target) < 10000000000000");
                    table.ForeignKey(
                        name: "fk_overdraft_recovery_snapshots_financial_accounts_account_id",
                        column: x => x.account_id,
                        principalTable: "financial_accounts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_financial_accounts_overdraft_limit",
                table: "financial_accounts",
                sql: "overdraft_limit IS NULL OR (overdraft_limit >= 0 AND overdraft_limit < 10000000000000 AND scale(overdraft_limit) <= 2)");

            migrationBuilder.CreateIndex(
                name: "ix_overdraft_recovery_snapshots_account_id",
                table: "overdraft_recovery_snapshots",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_overdraft_recovery_snapshots_workspace_id_account_id_compet",
                table: "overdraft_recovery_snapshots",
                columns: new[] { "workspace_id", "account_id", "competence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "overdraft_recovery_snapshots");

            migrationBuilder.DropCheckConstraint(
                name: "ck_financial_accounts_overdraft_limit",
                table: "financial_accounts");

            migrationBuilder.DropColumn(
                name: "overdraft_limit",
                table: "financial_accounts");
        }
    }
}
