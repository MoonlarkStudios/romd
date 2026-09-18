using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Romd.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AutomaticArtworkAcquisition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ArtworkOnly",
                schema: "romd",
                table: "Jobs",
                type: "boolean",
                nullable: true);

            migrationBuilder.Sql("UPDATE romd.\"Jobs\" SET \"ArtworkOnly\" = FALSE WHERE \"JobType\" = 'enrichment'");

            migrationBuilder.CreateTable(
                name: "ArtworkAcquisitions",
                schema: "romd",
                columns: table => new
                {
                    TitleId = table.Column<int>(type: "integer", nullable: false),
                    Role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SourceId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    UpdatedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArtworkAcquisitions", x => new { x.TitleId, x.Role });
                    table.ForeignKey(
                        name: "FK_ArtworkAcquisitions_Titles_TitleId",
                        column: x => x.TitleId,
                        principalSchema: "romd",
                        principalTable: "Titles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ArtworkEnrichmentSettings",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<Guid>(type: "uuid", nullable: false),
                    FillPosters = table.Column<bool>(type: "boolean", nullable: false),
                    FillHeroes = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArtworkEnrichmentSettings", x => x.Id);
                    table.CheckConstraint("CK_ArtworkEnrichmentSettings_Singleton", "\"Id\" = 1");
                });

            migrationBuilder.InsertData(
                schema: "romd",
                table: "ArtworkEnrichmentSettings",
                columns: new[] { "Id", "FillHeroes", "FillPosters", "Revision" },
                values: new object[] { 1, true, true, new Guid("00000000-0000-0000-0000-000000000000") });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArtworkAcquisitions",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "ArtworkEnrichmentSettings",
                schema: "romd");

            migrationBuilder.DropColumn(
                name: "ArtworkOnly",
                schema: "romd",
                table: "Jobs");
        }
    }
}
