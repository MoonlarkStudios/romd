using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Romd.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TitleProviderMatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE UNIQUE INDEX \"IX_TitleExternalIds_CanonicalProvider\" ON romd.\"TitleExternalIds\" (\"TitleId\", lower(\"Provider\"));");
            migrationBuilder.CreateTable(
                name: "TitleProviderMatchStates",
                schema: "romd",
                columns: table => new
                {
                    TitleId = table.Column<int>(type: "integer", nullable: false),
                    ProviderId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Revision = table.Column<Guid>(type: "uuid", nullable: false),
                    GameJson = table.Column<string>(type: "text", nullable: true),
                    SuppressAutomaticMatch = table.Column<bool>(type: "boolean", nullable: false),
                    IdentityChanged = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TitleProviderMatchStates", x => new { x.TitleId, x.ProviderId });
                    table.ForeignKey(
                        name: "FK_TitleProviderMatchStates_Titles_TitleId",
                        column: x => x.TitleId,
                        principalSchema: "romd",
                        principalTable: "Titles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX romd.\"IX_TitleExternalIds_CanonicalProvider\";");
            migrationBuilder.DropTable(
                name: "TitleProviderMatchStates",
                schema: "romd");
        }
    }
}
