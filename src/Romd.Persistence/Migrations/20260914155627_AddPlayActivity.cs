using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Romd.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlayActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlaySessions",
                schema: "romd",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TitleId = table.Column<int>(type: "integer", nullable: false),
                    ReleaseId = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<long>(type: "bigint", nullable: false),
                    EndedAt = table.Column<long>(type: "bigint", nullable: true),
                    ActiveDurationSeconds = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlaySessions", x => new { x.UserId, x.SessionId });
                    table.CheckConstraint("CK_PlaySessions_ActiveDuration", "\"ActiveDurationSeconds\" IS NULL OR (\"ActiveDurationSeconds\" >= 0 AND \"ActiveDurationSeconds\" <= 31622400)");
                    table.CheckConstraint("CK_PlaySessions_End", "\"EndedAt\" IS NULL OR \"EndedAt\" >= \"StartedAt\"");
                    table.ForeignKey(
                        name: "FK_PlaySessions_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalSchema: "romd",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlaySessions_UserId_StartedAt_SessionId",
                schema: "romd",
                table: "PlaySessions",
                columns: new[] { "UserId", "StartedAt", "SessionId" });

            migrationBuilder.CreateIndex(
                name: "IX_PlaySessions_UserId_TitleId_StartedAt",
                schema: "romd",
                table: "PlaySessions",
                columns: new[] { "UserId", "TitleId", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlaySessions",
                schema: "romd");
        }
    }
}
