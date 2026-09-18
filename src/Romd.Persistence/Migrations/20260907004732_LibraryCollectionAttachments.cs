using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Romd.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LibraryCollectionAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LibraryCollections",
                schema: "romd",
                columns: table => new
                {
                    LibraryId = table.Column<int>(type: "integer", nullable: false),
                    CollectionId = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsFeatured = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryCollections", x => new { x.LibraryId, x.CollectionId });
                    table.ForeignKey(
                        name: "FK_LibraryCollections_Collections_CollectionId",
                        column: x => x.CollectionId,
                        principalSchema: "romd",
                        principalTable: "Collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LibraryCollections_Libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalSchema: "romd",
                        principalTable: "Libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LibraryCollections_CollectionId",
                schema: "romd",
                table: "LibraryCollections",
                column: "CollectionId");

            // Preserve the previous global presentation for existing libraries only.
            // New libraries and collections require explicit attachment.
            migrationBuilder.Sql("""
                INSERT INTO romd."LibraryCollections" ("LibraryId", "CollectionId", "SortOrder", "IsFeatured")
                SELECT l."Id", c."Id", c."SortOrder", TRUE
                FROM romd."Libraries" l CROSS JOIN romd."Collections" c;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LibraryCollections",
                schema: "romd");
        }
    }
}
