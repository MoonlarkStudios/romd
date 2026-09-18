using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Romd.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExplicitAccountSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Legacy grants have no session identity. Require a fresh sign-in; do not invent sessions.
            migrationBuilder.Sql("UPDATE romd.\"OpenIddictTokens\" SET \"Status\" = 'revoked' WHERE \"Status\" = 'valid';");
            migrationBuilder.Sql("UPDATE romd.\"OpenIddictAuthorizations\" SET \"Status\" = 'revoked' WHERE \"Status\" = 'valid';");
            migrationBuilder.CreateTable(
                name: "AccountSessions",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Device = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    AccountStamp = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    LastUsedAt = table.Column<long>(type: "bigint", nullable: false),
                    ExpiresAt = table.Column<long>(type: "bigint", nullable: false),
                    RevokedAt = table.Column<long>(type: "bigint", nullable: true),
                    RevokedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    RevocationReason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountSessions_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalSchema: "romd",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountSessions_UserId_CreatedAt_Id",
                schema: "romd",
                table: "AccountSessions",
                columns: new[] { "UserId", "CreatedAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountSessions",
                schema: "romd");
        }
    }
}
