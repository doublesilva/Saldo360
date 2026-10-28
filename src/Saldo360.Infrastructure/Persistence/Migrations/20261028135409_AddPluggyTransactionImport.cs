using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPluggyTransactionImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pluggy_sync_runs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    accounts_total = table.Column<int>(type: "integer", nullable: false),
                    accounts_synced = table.Column<int>(type: "integer", nullable: false),
                    seen = table.Column<int>(type: "integer", nullable: false),
                    created = table.Column<int>(type: "integer", nullable: false),
                    updated = table.Column<int>(type: "integer", nullable: false),
                    unchanged = table.Column<int>(type: "integer", nullable: false),
                    missing = table.Column<int>(type: "integer", nullable: false),
                    errors = table.Column<int>(type: "integer", nullable: false),
                    error_summary = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pluggy_sync_runs", x => x.id);
                    table.CheckConstraint("ck_pluggy_sync_runs_counts", "seen >= 0 AND created >= 0 AND updated >= 0 AND unchanged >= 0 AND missing >= 0 AND errors >= 0");
                    table.CheckConstraint("ck_pluggy_sync_runs_finished", "(status = 'Running') = (finished_at IS NULL)");
                    table.CheckConstraint("ck_pluggy_sync_runs_status", "status IN ('Running','Completed','CompletedWithErrors','Failed')");
                    table.ForeignKey(
                        name: "fk_pluggy_sync_runs_application_users_user_id",
                        column: x => x.user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pluggy_sync_runs_financial_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "financial_workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pluggy_sync_runs_pluggy_item_links_connection_id",
                        column: x => x.connection_id,
                        principalTable: "pluggy_item_links",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "pluggy_imported_transactions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    pluggy_account_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    pluggy_transaction_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    account_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    account_label = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    date = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    currency = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    description_raw = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    operation_type = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    category = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    installment_number = table.Column<int>(type: "integer", nullable: true),
                    total_installments = table.Column<int>(type: "integer", nullable: true),
                    bill_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    card_last4 = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    payment_method = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    classification = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    classification_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    possible_duplicate_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    first_imported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    first_seen_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_changed_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    missing_sync_count = table.Column<int>(type: "integer", nullable: false),
                    first_missing_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pluggy_imported_transactions", x => x.id);
                    table.CheckConstraint("ck_pluggy_imported_transactions_class", "classification IN ('InsufficientInformation','PotentialIncome','PotentialExpense','PotentialTransfer','CardPurchase','CardInstallment')");
                    table.CheckConstraint("ck_pluggy_imported_transactions_missing", "missing_sync_count >= 0 AND (missing_sync_count = 0) = (first_missing_at IS NULL)");
                    table.CheckConstraint("ck_pluggy_imported_transactions_status", "status IN ('Pending','Posted','Unknown')");
                    table.ForeignKey(
                        name: "fk_pluggy_imported_transactions_financial_workspaces_workspace",
                        column: x => x.workspace_id,
                        principalTable: "financial_workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pluggy_imported_transactions_pluggy_item_links_connection_id",
                        column: x => x.connection_id,
                        principalTable: "pluggy_item_links",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pluggy_imported_transactions_pluggy_sync_runs_first_seen_ru",
                        column: x => x.first_seen_run_id,
                        principalTable: "pluggy_sync_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pluggy_imported_transactions_pluggy_sync_runs_last_changed_",
                        column: x => x.last_changed_run_id,
                        principalTable: "pluggy_sync_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pluggy_imported_transactions_pluggy_sync_runs_last_seen_run",
                        column: x => x.last_seen_run_id,
                        principalTable: "pluggy_sync_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "pluggy_transaction_changes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    workspace_id = table.Column<Guid>(type: "uuid", nullable: false),
                    transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sync_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    changed_fields = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    previous_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    new_status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    previous_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    new_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pluggy_transaction_changes", x => x.id);
                    table.ForeignKey(
                        name: "fk_pluggy_transaction_changes_financial_workspaces_workspace_id",
                        column: x => x.workspace_id,
                        principalTable: "financial_workspaces",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pluggy_transaction_changes_pluggy_imported_transactions_tra",
                        column: x => x.transaction_id,
                        principalTable: "pluggy_imported_transactions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_pluggy_transaction_changes_pluggy_sync_runs_sync_run_id",
                        column: x => x.sync_run_id,
                        principalTable: "pluggy_sync_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_pluggy_imported_transactions_connection_id_pluggy_account_i",
                table: "pluggy_imported_transactions",
                columns: new[] { "connection_id", "pluggy_account_id", "last_seen_run_id" });

            migrationBuilder.CreateIndex(
                name: "ix_pluggy_imported_transactions_first_seen_run_id",
                table: "pluggy_imported_transactions",
                column: "first_seen_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_pluggy_imported_transactions_last_changed_run_id",
                table: "pluggy_imported_transactions",
                column: "last_changed_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_pluggy_imported_transactions_last_seen_run_id",
                table: "pluggy_imported_transactions",
                column: "last_seen_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_pluggy_imported_transactions_workspace_id_connection_id_date",
                table: "pluggy_imported_transactions",
                columns: new[] { "workspace_id", "connection_id", "date" });

            migrationBuilder.CreateIndex(
                name: "ix_pluggy_imported_transactions_workspace_id_possible_duplicat",
                table: "pluggy_imported_transactions",
                columns: new[] { "workspace_id", "possible_duplicate_key" },
                filter: "possible_duplicate_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_pluggy_imported_transactions_external",
                table: "pluggy_imported_transactions",
                columns: new[] { "connection_id", "pluggy_account_id", "pluggy_transaction_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pluggy_sync_runs_connection_id_started_at",
                table: "pluggy_sync_runs",
                columns: new[] { "connection_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_pluggy_sync_runs_user_id",
                table: "pluggy_sync_runs",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_pluggy_sync_runs_workspace_id",
                table: "pluggy_sync_runs",
                column: "workspace_id");

            migrationBuilder.CreateIndex(
                name: "ux_pluggy_sync_runs_running",
                table: "pluggy_sync_runs",
                column: "connection_id",
                unique: true,
                filter: "status = 'Running'");

            migrationBuilder.CreateIndex(
                name: "ix_pluggy_transaction_changes_sync_run_id",
                table: "pluggy_transaction_changes",
                column: "sync_run_id");

            migrationBuilder.CreateIndex(
                name: "ix_pluggy_transaction_changes_transaction_id_observed_at",
                table: "pluggy_transaction_changes",
                columns: new[] { "transaction_id", "observed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_pluggy_transaction_changes_workspace_id",
                table: "pluggy_transaction_changes",
                column: "workspace_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pluggy_transaction_changes");

            migrationBuilder.DropTable(
                name: "pluggy_imported_transactions");

            migrationBuilder.DropTable(
                name: "pluggy_sync_runs");
        }
    }
}
