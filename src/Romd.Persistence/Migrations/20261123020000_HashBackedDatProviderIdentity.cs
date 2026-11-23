using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Romd.Persistence.Migrations;

[DbContext(typeof(RomdDbContext))]
[Migration("20261123020000_HashBackedDatProviderIdentity")]
public sealed class HashBackedDatProviderIdentity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE INDEX "IX_DatGames_DatFileId_DeclaredId_Id"
            ON romd."DatGames" ("DatFileId", "DeclaredId", "Id") WHERE "DeclaredId" IS NOT NULL;

            CREATE FUNCTION romd.provider_identity_dat_matches(title_id integer)
             RETURNS TABLE ("DatFileId" integer, "GameId" integer, "RomFileId" integer)
             LANGUAGE sql
             STABLE
            AS $function$
              WITH title_roms AS (
                SELECT DISTINCT r."Id", r."Sha1", t."PlatformId"
                FROM romd."Titles" t
                JOIN romd."TitleSourceLinks" l ON l."TitleId" = t."Id"
                JOIN romd."SourceEntries" s ON s."Id" = l."SourceEntryId"
                JOIN romd."CatalogSources" c ON c."Id" = s."CatalogSourceId" AND c."Status" = 'Active'
                JOIN romd."DatGames" g ON g."SourceEntryId" = s."Id" AND NOT g."IsBios"
                JOIN romd."DatFiles" d ON d."Id" = g."DatFileId" AND d."Lifecycle" = 'Active'
                JOIN romd."DatRoms" dr ON dr."DatGameId" = g."Id"
                JOIN romd."Roms" r ON r."Id" = dr."RomFileId" AND r."Sha1" = dr."Sha1"
                WHERE t."Id" = title_id AND d."PlatformId" = t."PlatformId"
              )
              SELECT DISTINCT g."DatFileId", g."Id", o."Id"
              FROM title_roms o
              JOIN romd."DatRoms" dr ON dr."RomFileId" = o."Id" AND dr."Sha1" = o."Sha1"
              JOIN romd."DatGames" g ON g."Id" = dr."DatGameId" AND NOT g."IsBios"
              JOIN romd."DatFiles" d ON d."Id" = g."DatFileId" AND d."Lifecycle" = 'Active'
                                    AND d."PlatformId" = o."PlatformId"
              JOIN romd."SourceEntries" s ON s."Id" = g."SourceEntryId"
              JOIN romd."CatalogSources" c ON c."Id" = s."CatalogSourceId" AND c."Status" = 'Active';
            $function$;
            """);
        migrationBuilder.Sql(FingerprintSql("""
             || COALESCE((
                SELECT jsonb_build_object('DatMatches', jsonb_agg(
                  jsonb_build_array(m."DatFileId", m."GameId", m."RomFileId")
                  ORDER BY m."DatFileId", m."GameId", m."RomFileId"))
                FROM romd.provider_identity_dat_matches(t."Id") m
                HAVING count(*) > 0
              ), '{}'::jsonb)
            """));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(FingerprintSql(string.Empty));
        migrationBuilder.Sql("""
            DROP FUNCTION romd.provider_identity_dat_matches(integer);
            DROP INDEX romd."IX_DatGames_DatFileId_DeclaredId_Id";
            """);
    }

    // A published DAT version pins an immutable declaration graph. The matched game
    // and logical ROM IDs also fence ownership changes within that version.
    // Keep the existing encoding for titles with no applicable ROM hash matches.
    private static string FingerprintSql(string datMatches) => $$"""
        CREATE OR REPLACE FUNCTION romd.provider_identity_fingerprint(title_id integer, policy_version text)
         RETURNS text
         LANGUAGE sql
         STABLE
        AS $function$
          SELECT encode(sha256(convert_to((jsonb_build_object(
            'Encoding', 'identity-db-1', 'Policy', policy_version,
            'Name', t."Name", 'Platform', jsonb_build_array(p."Name", p."ShortName"),
            'Local', COALESCE((
              SELECT jsonb_agg(jsonb_build_array(e.name, e.year) ORDER BY e.name COLLATE "C", e.year COLLATE "C")
              FROM (
                SELECT DISTINCT g."Name" AS name, g."Year" AS year
                FROM romd."TitleSourceLinks" l
                JOIN romd."SourceEntries" s ON s."Id" = l."SourceEntryId"
                JOIN romd."CatalogSources" c ON c."Id" = s."CatalogSourceId" AND c."Status" = 'Active'
                JOIN romd."DatGames" g ON g."SourceEntryId" = l."SourceEntryId"
                JOIN romd."DatFiles" d ON d."Id" = g."DatFileId" AND d."Lifecycle" = 'Active'
                WHERE l."TitleId" = t."Id" AND NOT g."IsBios"
              ) e
            ), '[]'::jsonb),
            'Confirmed', COALESCE((
              SELECT jsonb_agg(jsonb_build_array(e."Provider", e."ExternalId") ORDER BY e."Provider" COLLATE "C")
              FROM romd."TitleExternalIds" e WHERE e."TitleId" = t."Id" AND e."IsConfirmed"
            ), '[]'::jsonb)
          ){{datMatches}})::text, 'UTF8')), 'hex')
          FROM romd."Titles" t JOIN romd."Platforms" p ON p."Id" = t."PlatformId"
          WHERE t."Id" = title_id;
        $function$;
        """;
}
