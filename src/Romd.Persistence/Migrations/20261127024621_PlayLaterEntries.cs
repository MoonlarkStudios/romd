using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Romd.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PlayLaterEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlayLaterEntries",
                schema: "romd",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TitleId = table.Column<int>(type: "integer", nullable: false),
                    AddedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlayLaterEntries", x => new { x.UserId, x.TitleId });
                    table.ForeignKey(
                        name: "FK_PlayLaterEntries_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalSchema: "romd",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PlayLaterEntries_Titles_TitleId",
                        column: x => x.TitleId,
                        principalSchema: "romd",
                        principalTable: "Titles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlayLaterEntries_TitleId",
                schema: "romd",
                table: "PlayLaterEntries",
                column: "TitleId");

            migrationBuilder.CreateIndex(
                name: "IX_PlayLaterEntries_UserId_AddedAt_TitleId",
                schema: "romd",
                table: "PlayLaterEntries",
                columns: new[] { "UserId", "AddedAt", "TitleId" },
                descending: new[] { false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlayLaterEntries",
                schema: "romd");
        }
    }
}
