using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Romd.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GalleryArtworkCollection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TitleMedia_TitleId_Type_SourceId",
                schema: "romd",
                table: "TitleMedia");

            migrationBuilder.AddColumn<string>(
                name: "Attribution",
                schema: "romd",
                table: "TitleMedia",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourcePageUrl",
                schema: "romd",
                table: "TitleMedia",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TitleMedia_TitleId_Type_SourceId",
                schema: "romd",
                table: "TitleMedia",
                columns: new[] { "TitleId", "Type", "SourceId" },
                unique: true,
                filter: "\"SourceId\" <> 'user' AND \"SourceId\" NOT LIKE 'gallery:%'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TitleMedia_TitleId_Type_SourceId",
                schema: "romd",
                table: "TitleMedia");

            migrationBuilder.DropColumn(
                name: "Attribution",
                schema: "romd",
                table: "TitleMedia");

            migrationBuilder.DropColumn(
                name: "SourcePageUrl",
                schema: "romd",
                table: "TitleMedia");

            migrationBuilder.CreateIndex(
                name: "IX_TitleMedia_TitleId_Type_SourceId",
                schema: "romd",
                table: "TitleMedia",
                columns: new[] { "TitleId", "Type", "SourceId" },
                unique: true,
                filter: "\"SourceId\" <> 'user'");
        }
    }
}
