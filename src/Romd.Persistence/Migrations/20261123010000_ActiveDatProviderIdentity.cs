using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Romd.Persistence.Migrations;

[DbContext(typeof(RomdDbContext))]
[Migration("20261123010000_ActiveDatProviderIdentity")]
public sealed class ActiveDatProviderIdentity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(FingerprintSql("""
            JOIN romd."DatFiles" d ON d."Id" = g."DatFileId" AND d."Lifecycle" = 'Active'
            """));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(FingerprintSql(string.Empty));

    // Preserve the encoding for titles whose effective name and year facts are unchanged.
    private static string FingerprintSql(string versionJoin) => $$"""
        CREATE OR REPLACE FUNCTION romd.provider_identity_fingerprint(title_id integer, policy_version text)
         RETURNS text
         LANGUAGE sql
         STABLE
        AS $function$
          SELECT encode(sha256(convert_to(jsonb_build_object(
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
                {{versionJoin}}
                WHERE l."TitleId" = t."Id" AND NOT g."IsBios"
              ) e
            ), '[]'::jsonb),
            'Confirmed', COALESCE((
              SELECT jsonb_agg(jsonb_build_array(e."Provider", e."ExternalId") ORDER BY e."Provider" COLLATE "C")
              FROM romd."TitleExternalIds" e WHERE e."TitleId" = t."Id" AND e."IsConfirmed"
            ), '[]'::jsonb)
          )::text, 'UTF8')), 'hex')
          FROM romd."Titles" t JOIN romd."Platforms" p ON p."Id" = t."PlatformId"
          WHERE t."Id" = title_id;
        $function$;
        """;
}
