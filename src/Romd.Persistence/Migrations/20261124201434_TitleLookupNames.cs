using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace Romd.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TitleLookupNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TitleLookupNames",
                schema: "romd",
                columns: table => new
                {
                    TitleId = table.Column<int>(type: "integer", nullable: false),
                    SearchDocument = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SearchVector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: false, computedColumnSql: "to_tsvector('simple', \"SearchDocument\")", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TitleLookupNames", x => new { x.TitleId, x.SearchDocument });
                    table.ForeignKey(
                        name: "FK_TitleLookupNames_Titles_TitleId",
                        column: x => x.TitleId,
                        principalSchema: "romd",
                        principalTable: "Titles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TitleLookupNames_SearchVector",
                schema: "romd",
                table: "TitleLookupNames",
                column: "SearchVector")
                .Annotation("Npgsql:IndexMethod", "GIN");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TitleLookupNames",
                schema: "romd");
        }
    }
}
