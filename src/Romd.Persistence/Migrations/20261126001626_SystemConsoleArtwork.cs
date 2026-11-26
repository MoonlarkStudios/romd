using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Romd.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SystemConsoleArtwork : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BaseConsoleArtworkHash",
                schema: "romd",
                table: "Platforms",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConsoleArtworkOverrideHash",
                schema: "romd",
                table: "Platforms",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Platforms_BaseConsoleArtworkHash",
                schema: "romd",
                table: "Platforms",
                column: "BaseConsoleArtworkHash");

            migrationBuilder.CreateIndex(
                name: "IX_Platforms_ConsoleArtworkOverrideHash",
                schema: "romd",
                table: "Platforms",
                column: "ConsoleArtworkOverrideHash");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Platforms_ConsoleArtworkOwnership",
                schema: "romd",
                table: "Platforms",
                sql: "(\"BaseConsoleArtworkHash\" IS NULL OR \"Ownership\" IS NOT DISTINCT FROM 'Romd') AND (\"ConsoleArtworkOverrideHash\" IS NULL OR \"Ownership\" IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_Platforms_ReferenceAssets_BaseConsoleArtworkHash",
                schema: "romd",
                table: "Platforms",
                column: "BaseConsoleArtworkHash",
                principalSchema: "romd",
                principalTable: "ReferenceAssets",
                principalColumn: "Hash",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Platforms_ReferenceAssets_ConsoleArtworkOverrideHash",
                schema: "romd",
                table: "Platforms",
                column: "ConsoleArtworkOverrideHash",
                principalSchema: "romd",
                principalTable: "ReferenceAssets",
                principalColumn: "Hash",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Platforms_ReferenceAssets_BaseConsoleArtworkHash",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.DropForeignKey(
                name: "FK_Platforms_ReferenceAssets_ConsoleArtworkOverrideHash",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.DropIndex(
                name: "IX_Platforms_BaseConsoleArtworkHash",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.DropIndex(
                name: "IX_Platforms_ConsoleArtworkOverrideHash",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Platforms_ConsoleArtworkOwnership",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.DropColumn(
                name: "BaseConsoleArtworkHash",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.DropColumn(
                name: "ConsoleArtworkOverrideHash",
                schema: "romd",
                table: "Platforms");
        }
    }
}
