using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Saldo360.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIssuerToUserSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_user_sessions_subject",
                table: "user_sessions");

            migrationBuilder.AddColumn<string>(
                name: "issuer",
                table: "user_sessions",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_sessions_issuer_subject",
                table: "user_sessions",
                columns: new[] { "issuer", "subject" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_user_sessions_issuer_subject",
                table: "user_sessions");

            migrationBuilder.DropColumn(
                name: "issuer",
                table: "user_sessions");

            migrationBuilder.CreateIndex(
                name: "ix_user_sessions_subject",
                table: "user_sessions",
                column: "subject");
        }
    }
}
