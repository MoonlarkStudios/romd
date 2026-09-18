using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Romd.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TypedSystemsAndCompanies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Manufacturer",
                schema: "romd",
                table: "Platforms",
                type: "character varying(3230)",
                maxLength: 3230,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);

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

            migrationBuilder.CreateTable(
                name: "Companies",
                schema: "romd",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Metadata = table.Column<string>(type: "jsonb", nullable: false),
                    Definition = table.Column<string>(type: "jsonb", nullable: false),
                    Overrides = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Companies", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "SystemCompanies",
                schema: "romd",
                columns: table => new
                {
                    PlatformId = table.Column<int>(type: "integer", nullable: false),
                    CompanyKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemCompanies", x => new { x.PlatformId, x.CompanyKey });
                    table.ForeignKey(
                        name: "FK_SystemCompanies_Companies_CompanyKey",
                        column: x => x.CompanyKey,
                        principalSchema: "romd",
                        principalTable: "Companies",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SystemCompanies_Platforms_PlatformId",
                        column: x => x.PlatformId,
                        principalSchema: "romd",
                        principalTable: "Platforms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // Move the existing authoritative identities; never infer a system from a display name.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM romd."ReferenceDefinitions" d
                        WHERE d."Kind" = 'systems' AND NOT EXISTS (
                            SELECT 1 FROM romd."Platforms" p WHERE p."CanonicalKey" = d."Key"))
                    THEN RAISE EXCEPTION 'A reference system has no explicitly registered platform identity';
                    END IF;
                END $$;

                INSERT INTO romd."Companies" ("Key", "Metadata", "Definition", "Overrides")
                SELECT "Key",
                    jsonb_build_object('ownership', CASE "Ownership" WHEN 'Romd' THEN 0 ELSE 1 END,
                        'builtInVersion', "BuiltInVersion"),
                    jsonb_build_object('name', "DefinitionJson"->'name',
                        'description', "DefinitionJson"->'description',
                        'retired', COALESCE("DefinitionJson"->'retired', 'false'::jsonb)),
                    COALESCE((SELECT jsonb_object_agg(key, jsonb_build_object('value', value))
                        FROM jsonb_each(COALESCE("OverrideJson", '{}'::jsonb))), '{}'::jsonb)
                FROM romd."ReferenceDefinitions" WHERE "Kind" = 'companies';

                UPDATE romd."Platforms" p SET
                    "ReferenceMetadata" = jsonb_build_object(
                        'ownership', CASE d."Ownership" WHEN 'Romd' THEN 0 ELSE 1 END,
                        'builtInVersion', d."BuiltInVersion"),
                    "ReferenceDefinition" = jsonb_build_object(
                        'name', d."DefinitionJson"->'name',
                        'compactLabel', d."DefinitionJson"->'compactLabel',
                        'description', d."DefinitionJson"->'description',
                        'assetHash', d."DefinitionJson"->'assetHash',
                        'monochrome', COALESCE(d."DefinitionJson"->'monochrome', 'false'::jsonb),
                        'retired', COALESCE(d."DefinitionJson"->'retired', 'false'::jsonb)),
                    "ReferenceOverrides" = COALESCE(
                        (SELECT jsonb_object_agg(key, jsonb_build_object('value', value))
                         FROM jsonb_each(COALESCE(d."OverrideJson", '{}'::jsonb))), '{}'::jsonb)
                FROM romd."ReferenceDefinitions" d
                WHERE d."Kind" = 'systems' AND p."CanonicalKey" = d."Key";

                INSERT INTO romd."SystemCompanies" ("PlatformId", "CompanyKey")
                SELECT DISTINCT p."Id", company.key
                FROM romd."ReferenceDefinitions" d
                JOIN romd."Platforms" p ON p."CanonicalKey" = d."Key"
                CROSS JOIN LATERAL jsonb_array_elements_text(
                    CASE WHEN jsonb_typeof(d."DefinitionJson"->'manufacturerKeys') = 'array'
                        THEN d."DefinitionJson"->'manufacturerKeys' ELSE '[]'::jsonb END) company(key)
                WHERE d."Kind" = 'systems';

                DELETE FROM romd."ReferenceDefinitions" WHERE "Kind" IN ('systems', 'companies');
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ReferenceDefinitions_TypedIdentities",
                schema: "romd",
                table: "ReferenceDefinitions",
                sql: "\"Kind\" NOT IN ('systems', 'companies')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Platforms_ReferenceIdentity",
                schema: "romd",
                table: "Platforms",
                sql: "\"ReferenceDefinition\" IS NULL OR (\"CanonicalKey\" IS NOT NULL AND \"ReferenceMetadata\" IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_SystemCompanies_CompanyKey",
                schema: "romd",
                table: "SystemCompanies",
                column: "CompanyKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ReferenceDefinitions_TypedIdentities",
                schema: "romd",
                table: "ReferenceDefinitions");

            migrationBuilder.Sql("""
                INSERT INTO romd."ReferenceDefinitions" ("Kind", "Key", "Ownership", "BuiltInVersion", "DefinitionJson", "OverrideJson")
                SELECT 'companies', "Key",
                    CASE ("Metadata"->>'ownership')::int WHEN 0 THEN 'Romd' ELSE 'Installation' END,
                    ("Metadata"->>'builtInVersion')::int, "Definition",
                    (SELECT jsonb_object_agg(key, value->'value') FROM jsonb_each("Overrides") WHERE value <> 'null'::jsonb)
                FROM romd."Companies";

                INSERT INTO romd."ReferenceDefinitions" ("Kind", "Key", "Ownership", "BuiltInVersion", "DefinitionJson", "OverrideJson")
                SELECT 'systems', p."CanonicalKey",
                    CASE (p."ReferenceMetadata"->>'ownership')::int WHEN 0 THEN 'Romd' ELSE 'Installation' END,
                    (p."ReferenceMetadata"->>'builtInVersion')::int,
                    p."ReferenceDefinition" || jsonb_build_object('manufacturerKeys',
                        COALESCE((SELECT jsonb_agg(c."CompanyKey" ORDER BY c."CompanyKey")
                            FROM romd."SystemCompanies" c WHERE c."PlatformId" = p."Id"), '[]'::jsonb)),
                    (SELECT jsonb_object_agg(key, value->'value') FROM jsonb_each(p."ReferenceOverrides") WHERE value <> 'null'::jsonb)
                FROM romd."Platforms" p WHERE p."ReferenceDefinition" IS NOT NULL;
                """);

            migrationBuilder.DropTable(
                name: "SystemCompanies",
                schema: "romd");

            migrationBuilder.DropTable(
                name: "Companies",
                schema: "romd");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Platforms_ReferenceIdentity",
                schema: "romd",
                table: "Platforms");

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

            migrationBuilder.AlterColumn<string>(
                name: "Manufacturer",
                schema: "romd",
                table: "Platforms",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(3230)",
                oldMaxLength: 3230,
                oldNullable: true);
        }
    }
}
