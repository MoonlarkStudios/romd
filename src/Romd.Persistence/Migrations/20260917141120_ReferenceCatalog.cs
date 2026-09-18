using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Romd.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReferenceCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReferenceDataState",
                schema: "romd");

            migrationBuilder.DropIndex(
                name: "IX_Regions_Name",
                schema: "romd",
                table: "Regions");

            migrationBuilder.DropIndex(
                name: "IX_GameLanguages_Code",
                schema: "romd",
                table: "GameLanguages");

            migrationBuilder.DropIndex(
                name: "IX_GameLanguages_Name",
                schema: "romd",
                table: "GameLanguages");

            migrationBuilder.AddColumn<string>(
                name: "CanonicalKey",
                schema: "romd",
                table: "Regions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CanonicalKey",
                schema: "romd",
                table: "Platforms",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CanonicalKey",
                schema: "romd",
                table: "GameLanguages",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ReferenceAssets",
                schema: "romd",
                columns: table => new
                {
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FileId = table.Column<int>(type: "integer", nullable: false),
                    ReservedUntil = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReferenceAssets", x => x.Hash);
                    table.ForeignKey(
                        name: "FK_ReferenceAssets_Files_FileId",
                        column: x => x.FileId,
                        principalSchema: "romd",
                        principalTable: "Files",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReferenceCatalogState",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BuiltInVersion = table.Column<int>(type: "integer", nullable: false),
                    Json = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReferenceCatalogState", x => x.Id);
                    table.CheckConstraint("CK_ReferenceCatalogState_Singleton", "\"Id\" = 1");
                });

            migrationBuilder.CreateTable(
                name: "ReferenceDefinitions",
                schema: "romd",
                columns: table => new
                {
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Ownership = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    BuiltInVersion = table.Column<int>(type: "integer", nullable: true),
                    DefinitionJson = table.Column<string>(type: "jsonb", nullable: false),
                    OverrideJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReferenceDefinitions", x => new { x.Kind, x.Key });
                });

            migrationBuilder.CreateTable(
                name: "ReferenceAssetOwners",
                schema: "romd",
                columns: table => new
                {
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Slot = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReferenceAssetOwners", x => new { x.Kind, x.Key, x.Slot });
                    table.ForeignKey(
                        name: "FK_ReferenceAssetOwners_ReferenceAssets_Hash",
                        column: x => x.Hash,
                        principalSchema: "romd",
                        principalTable: "ReferenceAssets",
                        principalColumn: "Hash",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Regions_CanonicalKey",
                schema: "romd",
                table: "Regions",
                column: "CanonicalKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Regions_Name",
                schema: "romd",
                table: "Regions",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Platforms_CanonicalKey",
                schema: "romd",
                table: "Platforms",
                column: "CanonicalKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GameLanguages_CanonicalKey",
                schema: "romd",
                table: "GameLanguages",
                column: "CanonicalKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GameLanguages_Code",
                schema: "romd",
                table: "GameLanguages",
                column: "Code");

            migrationBuilder.CreateIndex(
                name: "IX_GameLanguages_Name",
                schema: "romd",
                table: "GameLanguages",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_ReferenceAssetOwners_Hash",
                schema: "romd",
                table: "ReferenceAssetOwners",
                column: "Hash");

            migrationBuilder.CreateIndex(
                name: "IX_ReferenceAssets_FileId",
                schema: "romd",
                table: "ReferenceAssets",
                column: "FileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReferenceAssetOwners",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "ReferenceCatalogState",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "ReferenceDefinitions",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "ReferenceAssets",
                schema: "romd");

            migrationBuilder.DropIndex(
                name: "IX_Regions_CanonicalKey",
                schema: "romd",
                table: "Regions");

            migrationBuilder.DropIndex(
                name: "IX_Regions_Name",
                schema: "romd",
                table: "Regions");

            migrationBuilder.DropIndex(
                name: "IX_Platforms_CanonicalKey",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.DropIndex(
                name: "IX_GameLanguages_CanonicalKey",
                schema: "romd",
                table: "GameLanguages");

            migrationBuilder.DropIndex(
                name: "IX_GameLanguages_Code",
                schema: "romd",
                table: "GameLanguages");

            migrationBuilder.DropIndex(
                name: "IX_GameLanguages_Name",
                schema: "romd",
                table: "GameLanguages");

            migrationBuilder.DropColumn(
                name: "CanonicalKey",
                schema: "romd",
                table: "Regions");

            migrationBuilder.DropColumn(
                name: "CanonicalKey",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.DropColumn(
                name: "CanonicalKey",
                schema: "romd",
                table: "GameLanguages");

            migrationBuilder.CreateTable(
                name: "ReferenceDataState",
                schema: "romd",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    BindingsJson = table.Column<string>(type: "text", nullable: false),
                    CanSeedFresh = table.Column<bool>(type: "boolean", nullable: false),
                    CandidateDocument = table.Column<string>(type: "text", nullable: true),
                    CandidateHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CandidateVersion = table.Column<long>(type: "bigint", nullable: true),
                    InitializedAt = table.Column<long>(type: "bigint", nullable: true),
                    InstalledDocument = table.Column<string>(type: "text", nullable: true),
                    InstalledHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    InstalledPublicationVersion = table.Column<long>(type: "bigint", nullable: false),
                    LastApplyResult = table.Column<string>(type: "text", nullable: true),
                    LastCheckMessage = table.Column<string>(type: "text", nullable: true),
                    LastCheckedAt = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReferenceDataState", x => x.Id);
                    table.CheckConstraint("CK_ReferenceDataState_Singleton", "\"Id\" = 1");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Regions_Name",
                schema: "romd",
                table: "Regions",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GameLanguages_Code",
                schema: "romd",
                table: "GameLanguages",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GameLanguages_Name",
                schema: "romd",
                table: "GameLanguages",
                column: "Name",
                unique: true);
        }
    }
}
