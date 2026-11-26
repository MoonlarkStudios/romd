using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Romd.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SystemControllerArtwork : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BaseControllerArtworkHash",
                schema: "romd",
                table: "Platforms",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ControllerArtworkOverrideHash",
                schema: "romd",
                table: "Platforms",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Platforms_BaseControllerArtworkHash",
                schema: "romd",
                table: "Platforms",
                column: "BaseControllerArtworkHash");

            migrationBuilder.CreateIndex(
                name: "IX_Platforms_ControllerArtworkOverrideHash",
                schema: "romd",
                table: "Platforms",
                column: "ControllerArtworkOverrideHash");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Platforms_ControllerArtworkOwnership",
                schema: "romd",
                table: "Platforms",
                sql: "(\"BaseControllerArtworkHash\" IS NULL OR \"Ownership\" IS NOT DISTINCT FROM 'Romd') AND (\"ControllerArtworkOverrideHash\" IS NULL OR \"Ownership\" IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_Platforms_ReferenceAssets_BaseControllerArtworkHash",
                schema: "romd",
                table: "Platforms",
                column: "BaseControllerArtworkHash",
                principalSchema: "romd",
                principalTable: "ReferenceAssets",
                principalColumn: "Hash",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Platforms_ReferenceAssets_ControllerArtworkOverrideHash",
                schema: "romd",
                table: "Platforms",
                column: "ControllerArtworkOverrideHash",
                principalSchema: "romd",
                principalTable: "ReferenceAssets",
                principalColumn: "Hash",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Platforms_ReferenceAssets_BaseControllerArtworkHash",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.DropForeignKey(
                name: "FK_Platforms_ReferenceAssets_ControllerArtworkOverrideHash",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.DropIndex(
                name: "IX_Platforms_BaseControllerArtworkHash",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.DropIndex(
                name: "IX_Platforms_ControllerArtworkOverrideHash",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Platforms_ControllerArtworkOwnership",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.DropColumn(
                name: "BaseControllerArtworkHash",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.DropColumn(
                name: "ControllerArtworkOverrideHash",
                schema: "romd",
                table: "Platforms");
        }
    }
}
