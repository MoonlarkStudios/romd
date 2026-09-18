using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Romd.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ArtworkFocalPoint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FocalX",
                schema: "romd",
                table: "ArtworkSelections",
                type: "integer",
                nullable: false,
                defaultValue: 50);

            migrationBuilder.AddColumn<int>(
                name: "FocalY",
                schema: "romd",
                table: "ArtworkSelections",
                type: "integer",
                nullable: false,
                defaultValue: 50);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ArtworkSelections_FocalPoint",
                schema: "romd",
                table: "ArtworkSelections",
                sql: "\"FocalX\" BETWEEN 0 AND 100 AND \"FocalY\" BETWEEN 0 AND 100");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ArtworkSelections_FocalPoint",
                schema: "romd",
                table: "ArtworkSelections");

            migrationBuilder.DropColumn(
                name: "FocalX",
                schema: "romd",
                table: "ArtworkSelections");

            migrationBuilder.DropColumn(
                name: "FocalY",
                schema: "romd",
                table: "ArtworkSelections");
        }
    }
}
