using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFinancialAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "ak_family_members_family_id_id",
                table: "family_members",
                columns: new[] { "family_id", "id" });

            migrationBuilder.CreateTable(
                name: "financial_accounts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    institution = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    scope = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    responsible_member_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_financial_accounts", x => x.id);
                    table.CheckConstraint("ck_financial_accounts_currency", "currency = 'BRL'");
                    table.CheckConstraint("ck_financial_accounts_name_not_blank", "btrim(name) <> ''");
                    table.CheckConstraint("ck_financial_accounts_scope", "scope IN ('PF', 'PJ')");
                    table.CheckConstraint("ck_financial_accounts_type", "type IN ('Checking', 'Savings', 'Cash')");
                    table.ForeignKey(
                        name: "fk_financial_accounts_application_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_accounts_families_family_id",
                        column: x => x.family_id,
                        principalTable: "families",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_financial_accounts_family_members_responsible",
                        columns: x => new { x.family_id, x.responsible_member_id },
                        principalTable: "family_members",
                        principalColumns: new[] { "family_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_financial_accounts_created_by_user_id",
                table: "financial_accounts",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_financial_accounts_family_id_is_active",
                table: "financial_accounts",
                columns: new[] { "family_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ix_financial_accounts_family_id_responsible_member_id",
                table: "financial_accounts",
                columns: new[] { "family_id", "responsible_member_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "financial_accounts");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_family_members_family_id_id",
                table: "family_members");
        }
    }
}
