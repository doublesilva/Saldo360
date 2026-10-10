using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFamilyInvitations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "family_invitations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    family_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    consumed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_family_invitations", x => x.id);
                    table.CheckConstraint("ck_family_invitations_consumed_by", "(consumed_at IS NULL) = (consumed_by_user_id IS NULL)");
                    table.CheckConstraint("ck_family_invitations_expiration", "expires_at > created_at");
                    table.CheckConstraint("ck_family_invitations_single_outcome", "consumed_at IS NULL OR revoked_at IS NULL");
                    table.CheckConstraint("ck_family_invitations_token_hash_size", "octet_length(token_hash) = 32");
                    table.ForeignKey(
                        name: "fk_family_invitations_application_users_consumed_by_user_id",
                        column: x => x.consumed_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_family_invitations_application_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "application_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_family_invitations_families_family_id",
                        column: x => x.family_id,
                        principalTable: "families",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_family_invitations_consumed_by_user_id",
                table: "family_invitations",
                column: "consumed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_family_invitations_created_by_user_id",
                table: "family_invitations",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_family_invitations_family_id",
                table: "family_invitations",
                column: "family_id");

            migrationBuilder.CreateIndex(
                name: "ix_family_invitations_token_hash",
                table: "family_invitations",
                column: "token_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "family_invitations");
        }
    }
}
