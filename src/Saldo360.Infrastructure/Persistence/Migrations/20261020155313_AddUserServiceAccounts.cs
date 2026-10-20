using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUserServiceAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "service_accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    issuer = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    display_name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_service_accounts", x => x.id);
                    table.UniqueConstraint("ak_service_accounts_id_user_id_service_type", x => new { x.id, x.user_id, x.service_type });
                    table.CheckConstraint("ck_service_accounts_names", "btrim(issuer) <> '' AND btrim(display_name) <> ''");
                    table.CheckConstraint("ck_service_accounts_type", "service_type IN ('Electricity','Water','Telecom')");
                    table.ForeignKey(
                        name: "fk_service_accounts_application_users_user_id",
                        column: x => x.user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "service_account_identifiers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    protected_normalized_value = table.Column<byte[]>(type: "bytea", nullable: false),
                    lookup_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    masked_value = table.Column<string>(type: "character varying(84)", maxLength: 84, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_service_account_identifiers", x => x.id);
                    table.CheckConstraint("ck_service_identifiers_protection", "octet_length(protected_normalized_value) > 0 AND lookup_hash ~ '^[0-9a-f]{64}$' AND btrim(masked_value) <> ''");
                    table.CheckConstraint("ck_service_identifiers_type", "type IN ('UtilityCustomerNumber','CustomerCode','InstallationNumber','ContractNumber','Keyword')");
                    table.ForeignKey(
                        name: "fk_service_account_identifiers_service_accounts_service_accoun",
                        columns: x => new { x.service_account_id, x.user_id, x.service_type },
                        principalTable: "service_accounts",
                        principalColumns: new[] { "id", "user_id", "service_type" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_service_account_identifiers_service_account_id_user_id_serv",
                table: "service_account_identifiers",
                columns: new[] { "service_account_id", "user_id", "service_type" });

            migrationBuilder.CreateIndex(
                name: "ux_service_identifiers_owner_type_hash_active",
                table: "service_account_identifiers",
                columns: new[] { "user_id", "service_type", "type", "lookup_hash" },
                unique: true,
                filter: "is_active AND type <> 'Keyword'");

            migrationBuilder.CreateIndex(
                name: "ix_service_accounts_user_id_service_type_is_active",
                table: "service_accounts",
                columns: new[] { "user_id", "service_type", "is_active" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "service_account_identifiers");

            migrationBuilder.DropTable(
                name: "service_accounts");
        }
    }
}
