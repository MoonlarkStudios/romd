using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Romd.Persistence.Migrations;

[DbContext(typeof(RomdDbContext))]
[Migration("20261123030000_ProvisionalSteamGridDbPosters")]
public sealed class ProvisionalSteamGridDbPosters : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>("IsProvisional", "ArtworkAssets", "boolean", schema: "romd",
            nullable: false, defaultValue: false);
        migrationBuilder.AddCheckConstraint("CK_ArtworkAssets_ProvisionalPoster", "ArtworkAssets",
            """NOT "IsProvisional" OR (NOT "IsEligible" AND "Role" = 'Poster' AND "SourceId" = 'steamgriddb' AND "ProviderGameId" IS NOT NULL AND "ProviderAssetId" IS NOT NULL)""",
            schema: "romd");
        migrationBuilder.Sql("""
            CREATE FUNCTION romd.provider_provisional_poster_game(
              title_id integer, provider_id text, policy_version text, poster_policy_version text)
            RETURNS text
            LANGUAGE sql
            STABLE
            AS $function$
              SELECT decision.value ->> 'ProvisionalGameId'
              FROM romd."TitleProviderMatchStates" candidate
              CROSS JOIN LATERAL (
                SELECT CASE WHEN candidate."DecisionJson" IS JSON OBJECT THEN candidate."DecisionJson"::jsonb END AS value
              ) decision
              JOIN romd."TitleExternalIds" source ON source."TitleId" = title_id AND source."Provider" = 'igdb'
              LEFT JOIN romd."TitleProviderMatchStates" source_state
                ON source_state."TitleId" = title_id AND source_state."ProviderId" = 'igdb'
              WHERE provider_id = 'steamgriddb'
                AND candidate."TitleId" = title_id AND candidate."ProviderId" = provider_id
                AND NOT candidate."SuppressAutomaticMatch" AND NOT candidate."AutomaticallyTrusted"
                AND candidate."MatcherVersion" = policy_version
                AND candidate."InputFingerprint" = romd.provider_identity_fingerprint(title_id, policy_version)
                AND decision.value ->> 'State' = 'NeedsReview'
                AND decision.value ->> 'PosterPolicyVersion' = poster_policy_version
                AND decision.value #>> '{Candidates,0,Game,Id}' = decision.value ->> 'ProvisionalGameId'
                AND decision.value #>> '{Dependencies,0,ProviderId}' = 'igdb'
                AND decision.value #>> '{Dependencies,0,GameId}' = source."ExternalId"
                AND decision.value #>> '{Dependencies,0,Revision}' =
                  COALESCE(source_state."Revision", '00000000-0000-0000-0000-000000000000'::uuid)::text
                AND NOT EXISTS (SELECT FROM romd."TitleExternalIds" existing
                  WHERE existing."TitleId" = title_id AND lower(existing."Provider") = provider_id)
                AND romd.provider_identity_trusted(title_id, 'igdb', policy_version);
            $function$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP FUNCTION romd.provider_provisional_poster_game(integer, text, text, text);");
        migrationBuilder.DropCheckConstraint("CK_ArtworkAssets_ProvisionalPoster", "ArtworkAssets", "romd");
        migrationBuilder.DropColumn("IsProvisional", "ArtworkAssets", "romd");
    }
}
