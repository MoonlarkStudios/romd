using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Romd.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ArtworkImportComposition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ArtworkFocalX",
                schema: "romd",
                table: "Jobs",
                type: "integer",
                nullable: true,
                defaultValue: 50);

            migrationBuilder.AddColumn<int>(
                name: "ArtworkFocalY",
                schema: "romd",
                table: "Jobs",
                type: "integer",
                nullable: true,
                defaultValue: 50);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ArtworkFocalX",
                schema: "romd",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ArtworkFocalY",
                schema: "romd",
                table: "Jobs");
        }
    }
}
