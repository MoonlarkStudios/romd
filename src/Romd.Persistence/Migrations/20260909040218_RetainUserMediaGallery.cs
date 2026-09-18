using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Romd.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RetainUserMediaGallery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TitleMedia_TitleId_Type_SourceId",
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TitleMedia_TitleId_Type_SourceId",
                schema: "romd",
                table: "TitleMedia");

            migrationBuilder.CreateIndex(
                name: "IX_TitleMedia_TitleId_Type_SourceId",
                schema: "romd",
                table: "TitleMedia",
                columns: new[] { "TitleId", "Type", "SourceId" },
                unique: true);
        }
    }
}
