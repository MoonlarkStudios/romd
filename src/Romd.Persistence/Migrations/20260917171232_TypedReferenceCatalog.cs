using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Romd.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TypedReferenceCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE conflicts text;
                BEGIN
                    SELECT string_agg("Kind" || '/' || "Key", ', ' ORDER BY "Kind", "Key") INTO conflicts
                    FROM romd."ReferenceDefinitions"
                    WHERE "Kind" IN ('regions', 'languages', 'ratingBoards', 'ratings')
                      AND ("Ownership" <> 'Romd' OR COALESCE("OverrideJson", '{}'::jsonb) <> '{}'::jsonb);
                    IF conflicts IS NOT NULL THEN
                        RAISE EXCEPTION 'Unsupported reference customization: %. Export and explicitly resolve these installation definitions or overrides before upgrading; no data has been discarded.', conflicts;
                    END IF;
                END $$;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_Platforms_ReferenceIdentity",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.AddColumn<string>(
                name: "BaseDescription",
                schema: "romd",
                table: "Regions",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BaseName",
                schema: "romd",
                table: "Regions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BaseSortOrder",
                schema: "romd",
                table: "Regions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BuiltInVersion",
                schema: "romd",
                table: "Regions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Ownership",
                schema: "romd",
                table: "Regions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Retired",
                schema: "romd",
                table: "Regions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Ownership",
                schema: "romd",
                table: "RegionAliases",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ArtworkOverrideHash",
                schema: "romd",
                table: "Platforms",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BaseAssetHash",
                schema: "romd",
                table: "Platforms",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BaseCompactLabel",
                schema: "romd",
                table: "Platforms",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BaseDescription",
                schema: "romd",
                table: "Platforms",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "BaseMonochrome",
                schema: "romd",
                table: "Platforms",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "BaseName",
                schema: "romd",
                table: "Platforms",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BuiltInVersion",
                schema: "romd",
                table: "Platforms",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CompactLabelOverride",
                schema: "romd",
                table: "Platforms",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DescriptionOverride",
                schema: "romd",
                table: "Platforms",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasArtworkOverride",
                schema: "romd",
                table: "Platforms",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HasDescriptionOverride",
                schema: "romd",
                table: "Platforms",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "MonochromeOverride",
                schema: "romd",
                table: "Platforms",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NameOverride",
                schema: "romd",
                table: "Platforms",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Ownership",
                schema: "romd",
                table: "Platforms",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Retired",
                schema: "romd",
                table: "Platforms",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Ownership",
                schema: "romd",
                table: "PlatformAliases",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BaseDescription",
                schema: "romd",
                table: "GameLanguages",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BaseName",
                schema: "romd",
                table: "GameLanguages",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "BaseSortOrder",
                schema: "romd",
                table: "GameLanguages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BuiltInVersion",
                schema: "romd",
                table: "GameLanguages",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Ownership",
                schema: "romd",
                table: "GameLanguages",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Retired",
                schema: "romd",
                table: "GameLanguages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Ownership",
                schema: "romd",
                table: "GameLanguageAliases",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BaseDescription",
                schema: "romd",
                table: "Companies",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BaseName",
                schema: "romd",
                table: "Companies",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "BuiltInVersion",
                schema: "romd",
                table: "Companies",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DescriptionOverride",
                schema: "romd",
                table: "Companies",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasDescriptionOverride",
                schema: "romd",
                table: "Companies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Name",
                schema: "romd",
                table: "Companies",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "NameOverride",
                schema: "romd",
                table: "Companies",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Ownership",
                schema: "romd",
                table: "Companies",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "Retired",
                schema: "romd",
                table: "Companies",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "RatingBoards",
                schema: "romd",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Ownership = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    BuiltInVersion = table.Column<int>(type: "integer", nullable: true),
                    BaseName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BaseDescription = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Retired = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RatingBoards", x => x.Key);
                    table.CheckConstraint("CK_RatingBoards_ReferenceOwnership", "(\"Ownership\" = 'Romd' AND \"BuiltInVersion\" IS NOT NULL AND \"BuiltInVersion\" > 0)");
                });

            migrationBuilder.CreateTable(
                name: "Ratings",
                schema: "romd",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Ownership = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    BuiltInVersion = table.Column<int>(type: "integer", nullable: true),
                    BaseName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BaseDescription = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Retired = table.Column<bool>(type: "boolean", nullable: false),
                    BoardKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Designation = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    MinimumAge = table.Column<int>(type: "integer", nullable: true),
                    BaseAssetHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    BaseMonochrome = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Ratings", x => x.Key);
                    table.CheckConstraint("CK_Ratings_Identity", "\"Key\" = \"BoardKey\" || ':' || \"Code\"");
                    table.CheckConstraint("CK_Ratings_MinimumAge", "\"MinimumAge\" IS NULL OR \"MinimumAge\" >= 0");
                    table.CheckConstraint("CK_Ratings_ReferenceOwnership", "(\"Ownership\" = 'Romd' AND \"BuiltInVersion\" IS NOT NULL AND \"BuiltInVersion\" > 0)");
                    table.ForeignKey(
                        name: "FK_Ratings_RatingBoards_BoardKey",
                        column: x => x.BoardKey,
                        principalSchema: "romd",
                        principalTable: "RatingBoards",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Ratings_ReferenceAssets_BaseAssetHash",
                        column: x => x.BaseAssetHash,
                        principalSchema: "romd",
                        principalTable: "ReferenceAssets",
                        principalColumn: "Hash",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql("""
                UPDATE romd."Platforms" SET
                    "Ownership" = CASE "ReferenceMetadata"->>'ownership' WHEN '0' THEN 'Romd' WHEN '1' THEN 'Installation' END,
                    "BuiltInVersion" = ("ReferenceMetadata"->>'builtInVersion')::integer,
                    "BaseName" = "ReferenceDefinition"->>'name', "BaseCompactLabel" = "ReferenceDefinition"->>'compactLabel',
                    "BaseDescription" = "ReferenceDefinition"->>'description', "BaseAssetHash" = "ReferenceDefinition"->>'assetHash',
                    "BaseMonochrome" = COALESCE(("ReferenceDefinition"->>'monochrome')::boolean, false),
                    "Retired" = COALESCE(("ReferenceDefinition"->>'retired')::boolean, false)
                WHERE "ReferenceDefinition" IS NOT NULL;
                UPDATE romd."Companies" SET
                    "Ownership" = CASE "Metadata"->>'ownership' WHEN '0' THEN 'Romd' WHEN '1' THEN 'Installation' END,
                    "BuiltInVersion" = ("Metadata"->>'builtInVersion')::integer,
                    "BaseName" = "Definition"->>'name', "BaseDescription" = "Definition"->>'description',
                    "Retired" = COALESCE(("Definition"->>'retired')::boolean, false);
                UPDATE romd."Regions" r SET "Ownership" = d."Ownership", "BuiltInVersion" = d."BuiltInVersion",
                    "BaseName" = d."DefinitionJson"->>'name', "BaseDescription" = d."DefinitionJson"->>'description',
                    "BaseSortOrder" = COALESCE((d."DefinitionJson"->>'sortOrder')::integer, 0),
                    "Retired" = COALESCE((d."DefinitionJson"->>'retired')::boolean, false )
                FROM romd."ReferenceDefinitions" d WHERE d."Kind" = 'regions' AND r."CanonicalKey" = d."Key";
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM romd."ReferenceDefinitions" d WHERE d."Kind" = 'regions' AND NOT EXISTS (SELECT 1 FROM romd."Regions" r WHERE r."CanonicalKey" = d."Key")) THEN RAISE EXCEPTION 'Unresolved regions reference identity'; END IF;
                END $$;
                UPDATE romd."GameLanguages" r SET "Ownership" = d."Ownership", "BuiltInVersion" = d."BuiltInVersion",
                    "BaseName" = d."DefinitionJson"->>'name', "BaseDescription" = d."DefinitionJson"->>'description',
                    "BaseSortOrder" = COALESCE((d."DefinitionJson"->>'sortOrder')::integer, 0),
                    "Retired" = COALESCE((d."DefinitionJson"->>'retired')::boolean, false )
                FROM romd."ReferenceDefinitions" d WHERE d."Kind" = 'languages' AND r."CanonicalKey" = d."Key";
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM romd."ReferenceDefinitions" d WHERE d."Kind" = 'languages' AND NOT EXISTS (SELECT 1 FROM romd."GameLanguages" r WHERE r."CanonicalKey" = d."Key")) THEN RAISE EXCEPTION 'Unresolved languages reference identity'; END IF;
                END $$;
                INSERT INTO romd."RatingBoards" ("Key", "Ownership", "BuiltInVersion", "BaseName", "BaseDescription", "Retired")
                SELECT d."Key", d."Ownership", d."BuiltInVersion", d."DefinitionJson"->>'name', d."DefinitionJson"->>'description', COALESCE((d."DefinitionJson"->>'retired')::boolean, false )
                FROM romd."ReferenceDefinitions" d WHERE d."Kind" = 'ratingBoards';
                INSERT INTO romd."Ratings" ("Key", "Ownership", "BuiltInVersion", "BaseName", "BaseDescription", "Retired", "BoardKey", "Code", "Designation", "MinimumAge", "BaseAssetHash", "BaseMonochrome")
                SELECT d."Key", d."Ownership", d."BuiltInVersion", d."DefinitionJson"->>'name', d."DefinitionJson"->>'description', COALESCE((d."DefinitionJson"->>'retired')::boolean, false ),
                    d."DefinitionJson"->>'board', d."DefinitionJson"->>'code', d."DefinitionJson"->>'designation', (d."DefinitionJson"->>'minimumAge')::integer, d."DefinitionJson"->>'assetHash', COALESCE((d."DefinitionJson"->>'monochrome')::boolean, false)
                FROM romd."ReferenceDefinitions" d WHERE d."Kind" = 'ratings';
                UPDATE romd."Platforms" SET
                    "NameOverride" = "ReferenceOverrides"->'name'->>'value',
                    "CompactLabelOverride" = "ReferenceOverrides"->'compactLabel'->>'value',
                    "HasDescriptionOverride" = COALESCE("ReferenceOverrides"->'description' <> 'null'::jsonb, false),
                    "DescriptionOverride" = "ReferenceOverrides"->'description'->>'value',
                    "HasArtworkOverride" = COALESCE("ReferenceOverrides"->'icon' <> 'null'::jsonb, false),
                    "ArtworkOverrideHash" = "ReferenceOverrides"->'icon'->>'value',
                    "MonochromeOverride" = ("ReferenceOverrides"->'monochrome'->>'value')::boolean;
                UPDATE romd."Companies" SET
                    "NameOverride" = "Overrides"->'name'->>'value',
                    "HasDescriptionOverride" = COALESCE("Overrides"->'description' <> 'null'::jsonb, false),
                    "DescriptionOverride" = "Overrides"->'description'->>'value',
                    "Name" = COALESCE("Overrides"->'name'->>'value', "BaseName");
                """);

            migrationBuilder.DropTable(
                name: "ReferenceDefinitions",
                schema: "romd");

            migrationBuilder.DropColumn(
                name: "ReferenceDefinition",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.DropColumn(
                name: "ReferenceMetadata",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.DropColumn(
                name: "ReferenceOverrides",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.DropColumn(
                name: "Definition",
                schema: "romd",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "Metadata",
                schema: "romd",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "Overrides",
                schema: "romd",
                table: "Companies");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Regions_BaseSortOrder",
                schema: "romd",
                table: "Regions",
                sql: "\"BaseSortOrder\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Regions_ReferenceIdentity",
                schema: "romd",
                table: "Regions",
                sql: "(\"CanonicalKey\" IS NULL AND \"Ownership\" IS NULL AND \"BaseName\" IS NULL AND \"BaseDescription\" IS NULL AND NOT \"Retired\" AND \"BaseSortOrder\" = 0) OR (\"CanonicalKey\" IS NOT NULL AND \"Ownership\" IS NOT NULL AND \"BaseName\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Regions_ReferenceOwnership",
                schema: "romd",
                table: "Regions",
                sql: "CASE WHEN \"Ownership\" IS NULL THEN \"BuiltInVersion\" IS NULL WHEN \"Ownership\" = 'Romd' THEN \"BuiltInVersion\" IS NOT NULL AND \"BuiltInVersion\" > 0 ELSE false END");

            migrationBuilder.CreateIndex(
                name: "IX_Platforms_ArtworkOverrideHash",
                schema: "romd",
                table: "Platforms",
                column: "ArtworkOverrideHash");

            migrationBuilder.CreateIndex(
                name: "IX_Platforms_BaseAssetHash",
                schema: "romd",
                table: "Platforms",
                column: "BaseAssetHash");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Platforms_ArtworkOverride",
                schema: "romd",
                table: "Platforms",
                sql: "\"HasArtworkOverride\" OR \"ArtworkOverrideHash\" IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Platforms_DescriptionOverride",
                schema: "romd",
                table: "Platforms",
                sql: "\"HasDescriptionOverride\" OR \"DescriptionOverride\" IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Platforms_ReferenceIdentity",
                schema: "romd",
                table: "Platforms",
                sql: "(\"CanonicalKey\" IS NULL AND \"Ownership\" IS NULL AND \"BaseName\" IS NULL AND \"BaseCompactLabel\" IS NULL AND \"BaseDescription\" IS NULL AND \"BaseAssetHash\" IS NULL AND NOT \"Retired\" AND NOT \"BaseMonochrome\") OR (\"CanonicalKey\" IS NOT NULL AND \"Ownership\" IS NOT NULL AND \"BaseName\" IS NOT NULL AND \"BaseCompactLabel\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Platforms_ReferenceOwnership",
                schema: "romd",
                table: "Platforms",
                sql: "CASE WHEN \"Ownership\" IS NULL THEN \"BuiltInVersion\" IS NULL WHEN \"Ownership\" = 'Romd' THEN \"BuiltInVersion\" IS NOT NULL AND \"BuiltInVersion\" > 0 ELSE \"Ownership\" = 'Installation' AND \"BuiltInVersion\" IS NULL END");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GameLanguages_BaseSortOrder",
                schema: "romd",
                table: "GameLanguages",
                sql: "\"BaseSortOrder\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GameLanguages_ReferenceIdentity",
                schema: "romd",
                table: "GameLanguages",
                sql: "(\"CanonicalKey\" IS NULL AND \"Ownership\" IS NULL AND \"BaseName\" IS NULL AND \"BaseDescription\" IS NULL AND NOT \"Retired\" AND \"BaseSortOrder\" = 0) OR (\"CanonicalKey\" IS NOT NULL AND \"Ownership\" IS NOT NULL AND \"BaseName\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_GameLanguages_ReferenceOwnership",
                schema: "romd",
                table: "GameLanguages",
                sql: "CASE WHEN \"Ownership\" IS NULL THEN \"BuiltInVersion\" IS NULL WHEN \"Ownership\" = 'Romd' THEN \"BuiltInVersion\" IS NOT NULL AND \"BuiltInVersion\" > 0 ELSE false END");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Companies_DescriptionOverride",
                schema: "romd",
                table: "Companies",
                sql: "\"HasDescriptionOverride\" OR \"DescriptionOverride\" IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Companies_ReferenceOwnership",
                schema: "romd",
                table: "Companies",
                sql: "(\"Ownership\" = 'Romd' AND \"BuiltInVersion\" IS NOT NULL AND \"BuiltInVersion\" > 0) OR (\"Ownership\" = 'Installation' AND \"BuiltInVersion\" IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_Ratings_BaseAssetHash",
                schema: "romd",
                table: "Ratings",
                column: "BaseAssetHash");

            migrationBuilder.CreateIndex(
                name: "IX_Ratings_BoardKey_Code",
                schema: "romd",
                table: "Ratings",
                columns: new[] { "BoardKey", "Code" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Platforms_ReferenceAssets_ArtworkOverrideHash",
                schema: "romd",
                table: "Platforms",
                column: "ArtworkOverrideHash",
                principalSchema: "romd",
                principalTable: "ReferenceAssets",
                principalColumn: "Hash",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Platforms_ReferenceAssets_BaseAssetHash",
                schema: "romd",
                table: "Platforms",
                column: "BaseAssetHash",
                principalSchema: "romd",
                principalTable: "ReferenceAssets",
                principalColumn: "Hash",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Platforms_ReferenceAssets_ArtworkOverrideHash",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.DropForeignKey(
                name: "FK_Platforms_ReferenceAssets_BaseAssetHash",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Regions_BaseSortOrder",
                schema: "romd",
                table: "Regions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Regions_ReferenceIdentity",
                schema: "romd",
                table: "Regions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Regions_ReferenceOwnership",
                schema: "romd",
                table: "Regions");

            migrationBuilder.DropIndex(
                name: "IX_Platforms_ArtworkOverrideHash",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.DropIndex(
                name: "IX_Platforms_BaseAssetHash",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Platforms_ArtworkOverride",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Platforms_DescriptionOverride",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Platforms_ReferenceIdentity",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Platforms_ReferenceOwnership",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GameLanguages_BaseSortOrder",
                schema: "romd",
                table: "GameLanguages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GameLanguages_ReferenceIdentity",
                schema: "romd",
                table: "GameLanguages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_GameLanguages_ReferenceOwnership",
                schema: "romd",
                table: "GameLanguages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Companies_DescriptionOverride",
                schema: "romd",
                table: "Companies");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Companies_ReferenceOwnership",
                schema: "romd",
                table: "Companies");

            migrationBuilder.AddColumn<string>(
                name: "ReferenceDefinition",
                schema: "romd",
                table: "Platforms",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReferenceMetadata",
                schema: "romd",
                table: "Platforms",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReferenceOverrides",
                schema: "romd",
                table: "Platforms",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Definition",
                schema: "romd",
                table: "Companies",
                type: "jsonb",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Metadata",
                schema: "romd",
                table: "Companies",
                type: "jsonb",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Overrides",
                schema: "romd",
                table: "Companies",
                type: "jsonb",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "ReferenceDefinitions",
                schema: "romd",
                columns: table => new
                {
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    BuiltInVersion = table.Column<int>(type: "integer", nullable: true),
                    DefinitionJson = table.Column<string>(type: "jsonb", nullable: false),
                    OverrideJson = table.Column<string>(type: "jsonb", nullable: true),
                    Ownership = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReferenceDefinitions", x => new { x.Kind, x.Key });
                    table.CheckConstraint("CK_ReferenceDefinitions_TypedIdentities", "\"Kind\" NOT IN ('systems', 'companies')");
                });

            migrationBuilder.Sql("""
                UPDATE romd."Platforms" SET
                    "ReferenceMetadata" = jsonb_build_object('ownership', CASE "Ownership" WHEN 'Romd' THEN 0 ELSE 1 END, 'builtInVersion', "BuiltInVersion"),
                    "ReferenceDefinition" = jsonb_build_object('name', "BaseName", 'compactLabel', "BaseCompactLabel", 'description', "BaseDescription", 'assetHash', "BaseAssetHash", 'monochrome', "BaseMonochrome", 'retired', "Retired")
                WHERE "Ownership" IS NOT NULL;
                UPDATE romd."Companies" SET
                    "Metadata" = jsonb_build_object('ownership', CASE "Ownership" WHEN 'Romd' THEN 0 ELSE 1 END, 'builtInVersion', "BuiltInVersion"),
                    "Definition" = jsonb_build_object('name', "BaseName", 'description', "BaseDescription", 'retired', "Retired");
                INSERT INTO romd."ReferenceDefinitions" ("Kind", "Key", "Ownership", "BuiltInVersion", "DefinitionJson", "OverrideJson")
                SELECT 'regions', r."CanonicalKey", r."Ownership", r."BuiltInVersion", jsonb_build_object('name', r."BaseName", 'description', r."BaseDescription", 'retired', r."Retired", 'sortOrder', r."BaseSortOrder"), NULL
                FROM romd."Regions" r WHERE r."Ownership" IS NOT NULL;
                INSERT INTO romd."ReferenceDefinitions" ("Kind", "Key", "Ownership", "BuiltInVersion", "DefinitionJson", "OverrideJson")
                SELECT 'languages', r."CanonicalKey", r."Ownership", r."BuiltInVersion", jsonb_build_object('name', r."BaseName", 'description', r."BaseDescription", 'retired', r."Retired", 'sortOrder', r."BaseSortOrder", 'languageCode', r."Code"), NULL
                FROM romd."GameLanguages" r WHERE r."Ownership" IS NOT NULL;
                INSERT INTO romd."ReferenceDefinitions" ("Kind", "Key", "Ownership", "BuiltInVersion", "DefinitionJson", "OverrideJson")
                SELECT 'ratingBoards', r."Key", r."Ownership", r."BuiltInVersion", jsonb_build_object('name', r."BaseName", 'description', r."BaseDescription", 'retired', r."Retired"), NULL
                FROM romd."RatingBoards" r;
                INSERT INTO romd."ReferenceDefinitions" ("Kind", "Key", "Ownership", "BuiltInVersion", "DefinitionJson", "OverrideJson")
                SELECT 'ratings', r."Key", r."Ownership", r."BuiltInVersion", jsonb_build_object('name', r."BaseName", 'description', r."BaseDescription", 'retired', r."Retired", 'board', r."BoardKey", 'code', r."Code", 'designation', r."Designation", 'minimumAge', r."MinimumAge", 'assetHash', r."BaseAssetHash", 'monochrome', r."BaseMonochrome"), NULL
                FROM romd."Ratings" r;
                UPDATE romd."Platforms" SET "ReferenceOverrides" = jsonb_build_object(
                    'name', CASE WHEN "NameOverride" IS NOT NULL THEN jsonb_build_object('value', "NameOverride") END,
                    'compactLabel', CASE WHEN "CompactLabelOverride" IS NOT NULL THEN jsonb_build_object('value', "CompactLabelOverride") END,
                    'description', CASE WHEN "HasDescriptionOverride" THEN jsonb_build_object('value', "DescriptionOverride") END,
                    'icon', CASE WHEN "HasArtworkOverride" THEN jsonb_build_object('value', "ArtworkOverrideHash") END,
                    'monochrome', CASE WHEN "MonochromeOverride" IS NOT NULL THEN jsonb_build_object('value', "MonochromeOverride") END)
                WHERE "Ownership" IS NOT NULL;
                UPDATE romd."Companies" SET "Overrides" = jsonb_build_object(
                    'name', CASE WHEN "NameOverride" IS NOT NULL THEN jsonb_build_object('value', "NameOverride") END,
                    'description', CASE WHEN "HasDescriptionOverride" THEN jsonb_build_object('value', "DescriptionOverride") END);
                """);

            migrationBuilder.DropTable(
                name: "Ratings",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "RatingBoards",
                schema: "romd");

            migrationBuilder.DropColumn(
                name: "BaseDescription",
                schema: "romd",
                table: "Regions");

            migrationBuilder.DropColumn(
                name: "BaseName",
                schema: "romd",
                table: "Regions");

            migrationBuilder.DropColumn(
                name: "BaseSortOrder",
                schema: "romd",
                table: "Regions");

            migrationBuilder.DropColumn(
                name: "BuiltInVersion",
                schema: "romd",
                table: "Regions");

            migrationBuilder.DropColumn(
                name: "Ownership",
                schema: "romd",
                table: "Regions");

            migrationBuilder.DropColumn(
                name: "Retired",
                schema: "romd",
                table: "Regions");

            migrationBuilder.DropColumn(
                name: "Ownership",
                schema: "romd",
                table: "RegionAliases");

            migrationBuilder.DropColumn(
                name: "ArtworkOverrideHash",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.DropColumn(
                name: "BaseAssetHash",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.DropColumn(
                name: "BaseCompactLabel",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.DropColumn(
                name: "BaseDescription",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.DropColumn(
                name: "BaseMonochrome",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.DropColumn(
                name: "BaseName",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.DropColumn(
                name: "BuiltInVersion",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.DropColumn(
                name: "CompactLabelOverride",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.DropColumn(
                name: "DescriptionOverride",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.DropColumn(
                name: "HasArtworkOverride",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.DropColumn(
                name: "HasDescriptionOverride",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.DropColumn(
                name: "MonochromeOverride",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.DropColumn(
                name: "NameOverride",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.DropColumn(
                name: "Ownership",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.DropColumn(
                name: "Retired",
                schema: "romd",
                table: "Platforms");

            migrationBuilder.DropColumn(
                name: "Ownership",
                schema: "romd",
                table: "PlatformAliases");

            migrationBuilder.DropColumn(
                name: "BaseDescription",
                schema: "romd",
                table: "GameLanguages");

            migrationBuilder.DropColumn(
                name: "BaseName",
                schema: "romd",
                table: "GameLanguages");

            migrationBuilder.DropColumn(
                name: "BaseSortOrder",
                schema: "romd",
                table: "GameLanguages");

            migrationBuilder.DropColumn(
                name: "BuiltInVersion",
                schema: "romd",
                table: "GameLanguages");

            migrationBuilder.DropColumn(
                name: "Ownership",
                schema: "romd",
                table: "GameLanguages");

            migrationBuilder.DropColumn(
                name: "Retired",
                schema: "romd",
                table: "GameLanguages");

            migrationBuilder.DropColumn(
                name: "Ownership",
                schema: "romd",
                table: "GameLanguageAliases");

            migrationBuilder.DropColumn(
                name: "BaseDescription",
                schema: "romd",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "BaseName",
                schema: "romd",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "BuiltInVersion",
                schema: "romd",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "DescriptionOverride",
                schema: "romd",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "HasDescriptionOverride",
                schema: "romd",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "Name",
                schema: "romd",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "NameOverride",
                schema: "romd",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "Ownership",
                schema: "romd",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "Retired",
                schema: "romd",
                table: "Companies");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Platforms_ReferenceIdentity",
                schema: "romd",
                table: "Platforms",
                sql: "\"ReferenceDefinition\" IS NULL OR (\"CanonicalKey\" IS NOT NULL AND \"ReferenceMetadata\" IS NOT NULL)");
        }
    }
}
