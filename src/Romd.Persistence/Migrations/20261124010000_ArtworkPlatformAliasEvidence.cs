using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Romd.Persistence.Migrations;

[DbContext(typeof(RomdDbContext))]
[Migration("20261124010000_ArtworkPlatformAliasEvidence")]
public sealed class ArtworkPlatformAliasEvidence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE FUNCTION romd.provider_identity_owned_platform_aliases(title_id integer)
             RETURNS jsonb
             LANGUAGE sql STABLE
            AS $function$
              SELECT coalesce(jsonb_agg(jsonb_build_object('Id', alias."Id", 'Name', alias."Value") ORDER BY alias."Id"), '[]'::jsonb)
              FROM romd."Titles" t
              JOIN romd."PlatformAliases" alias ON alias."PlatformId" = t."PlatformId"
              WHERE t."Id" = title_id AND alias."Type" = 0 AND alias."Ownership" = 'Romd';
            $function$;
            """);
        migrationBuilder.Sql(DatParentProviderIdentity.TrustWalkWithDatParent("""
              IF decision ? 'ArtworkPlatformAliases' AND (
                provider_id <> 'steamgriddb'
                OR decision -> 'ArtworkPlatformAliases' = '[]'::jsonb
                OR decision -> 'ArtworkPlatformAliases' IS DISTINCT FROM romd.provider_identity_owned_platform_aliases(title_id)
                OR NOT coalesce((decision -> 'Dependencies') @> '[{"ProviderId":"igdb"}]'::jsonb, false)
              ) THEN RETURN false; END IF;
            """));
        migrationBuilder.Sql(DatParentProviderIdentity.CreateArtworkCurrent("CREATE OR REPLACE",
            "?| ARRAY['DatParentMatch', 'ArtworkPlatformAliases']"));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Restore the previous reader without exposing retained artwork that used the removed proof.
        migrationBuilder.Sql("""
            UPDATE romd."ArtworkAssets" asset SET "IsEligible" = false
            FROM romd."TitleProviderMatchStates" state
            JOIN romd."TitleExternalIds" link ON link."TitleId" = state."TitleId" AND link."Provider" = state."ProviderId"
            WHERE asset."TitleId" = state."TitleId" AND asset."SourceId" = state."ProviderId"
              AND asset."ProviderGameId" = link."ExternalId" AND NOT link."IsConfirmed"
              AND state."DecisionJson"::jsonb ? 'ArtworkPlatformAliases';
            UPDATE romd."TitleProviderMatchStates" SET "AutomaticallyTrusted" = false
            WHERE "DecisionJson"::jsonb ? 'ArtworkPlatformAliases';
            """);
        migrationBuilder.Sql(DatParentProviderIdentity.CreateArtworkCurrent("CREATE OR REPLACE", "? 'DatParentMatch'"));
        migrationBuilder.Sql(DatParentProviderIdentity.TrustWalkWithDatParent());
        migrationBuilder.Sql("DROP FUNCTION romd.provider_identity_owned_platform_aliases(integer);");
    }
}
