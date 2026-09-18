using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Romd.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ArtworkFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ArtworkAssets",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TitleId = table.Column<int>(type: "integer", nullable: false),
                    Role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SourceId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ProviderGameId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ProviderAssetId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    OriginalFileId = table.Column<int>(type: "integer", nullable: false),
                    ContentVersion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    IsEligible = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                    Attribution = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SourcePageUrl = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArtworkAssets", x => x.Id);
                    table.UniqueConstraint("AK_ArtworkAssets_Id_TitleId_Role", x => new { x.Id, x.TitleId, x.Role });
                    table.CheckConstraint("CK_ArtworkAssets_Dimensions", "\"Width\" > 0 AND \"Height\" > 0");
                    table.CheckConstraint("CK_ArtworkAssets_Role", "\"Role\" IN ('Poster', 'Hero')");
                    table.ForeignKey(
                        name: "FK_ArtworkAssets_Files_OriginalFileId",
                        column: x => x.OriginalFileId,
                        principalSchema: "romd",
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ArtworkAssets_Titles_TitleId",
                        column: x => x.TitleId,
                        principalSchema: "romd",
                        principalTable: "Titles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ArtworkPreferences",
                schema: "romd",
                columns: table => new
                {
                    Role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SourceId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArtworkPreferences", x => new { x.Role, x.SourceId });
                    table.CheckConstraint("CK_ArtworkPreferences_Priority", "\"Priority\" >= 0");
                    table.CheckConstraint("CK_ArtworkPreferences_Role", "\"Role\" IN ('Poster', 'Hero')");
                });

            migrationBuilder.CreateTable(
                name: "ArtworkSelections",
                schema: "romd",
                columns: table => new
                {
                    TitleId = table.Column<int>(type: "integer", nullable: false),
                    Role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PinnedAssetId = table.Column<int>(type: "integer", nullable: true),
                    PendingRequestId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArtworkSelections", x => new { x.TitleId, x.Role });
                    table.CheckConstraint("CK_ArtworkSelections_Mode", "(\"Mode\" = 'Automatic' AND \"PinnedAssetId\" IS NULL) OR (\"Mode\" = 'Pinned' AND \"PinnedAssetId\" IS NOT NULL)");
                    table.CheckConstraint("CK_ArtworkSelections_Revision", "\"Revision\" >= 0");
                    table.CheckConstraint("CK_ArtworkSelections_Role", "\"Role\" IN ('Poster', 'Hero')");
                    table.ForeignKey(
                        name: "FK_ArtworkSelections_ArtworkAssets_PinnedAssetId_TitleId_Role",
                        columns: x => new { x.PinnedAssetId, x.TitleId, x.Role },
                        principalSchema: "romd",
                        principalTable: "ArtworkAssets",
                        principalColumns: new[] { "Id", "TitleId", "Role" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ArtworkSelections_Titles_TitleId",
                        column: x => x.TitleId,
                        principalSchema: "romd",
                        principalTable: "Titles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ArtworkVariants",
                schema: "romd",
                columns: table => new
                {
                    AssetId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    FileId = table.Column<int>(type: "integer", nullable: false),
                    ContentVersion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArtworkVariants", x => new { x.AssetId, x.Name });
                    table.CheckConstraint("CK_ArtworkVariants_Dimensions", "\"Width\" > 0 AND \"Height\" > 0");
                    table.ForeignKey(
                        name: "FK_ArtworkVariants_ArtworkAssets_AssetId",
                        column: x => x.AssetId,
                        principalSchema: "romd",
                        principalTable: "ArtworkAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ArtworkVariants_Files_FileId",
                        column: x => x.FileId,
                        principalSchema: "romd",
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArtworkAssets_OriginalFileId",
                schema: "romd",
                table: "ArtworkAssets",
                column: "OriginalFileId");

            migrationBuilder.CreateIndex(
                name: "IX_ArtworkAssets_TitleId_Role_SourceId_ProviderAssetId_Content~",
                schema: "romd",
                table: "ArtworkAssets",
                columns: new[] { "TitleId", "Role", "SourceId", "ProviderAssetId", "ContentVersion" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_ArtworkPreferences_Role_Priority",
                schema: "romd",
                table: "ArtworkPreferences",
                columns: new[] { "Role", "Priority" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ArtworkSelections_PinnedAssetId_TitleId_Role",
                schema: "romd",
                table: "ArtworkSelections",
                columns: new[] { "PinnedAssetId", "TitleId", "Role" });

            migrationBuilder.CreateIndex(
                name: "IX_ArtworkVariants_FileId",
                schema: "romd",
                table: "ArtworkVariants",
                column: "FileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArtworkPreferences",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "ArtworkSelections",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "ArtworkVariants",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "ArtworkAssets",
                schema: "romd");
        }
    }
}
