using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Romd.Persistence.Migrations;

[DbContext(typeof(RomdDbContext))]
[Migration("20261124000000_DatParentProviderIdentity")]
public sealed class DatParentProviderIdentity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE FUNCTION romd.provider_identity_dat_parent_title(dat_file_id integer, dat_game_id integer)
             RETURNS integer
             LANGUAGE sql STABLE
            AS $function$
              SELECT link."TitleId"
              FROM romd."DatGames" game
              JOIN romd."TitleSourceLinks" link ON link."SourceEntryId" = game."SourceEntryId"
              WHERE game."Id" = dat_game_id AND game."DatFileId" = dat_file_id
                AND (
                  SELECT bool_and(m."DatFileId" = dat_file_id AND m."GameId" = dat_game_id)
                  FROM romd.provider_identity_dat_matches(link."TitleId") m
                );
            $function$;
            """);
        migrationBuilder.Sql(TrustWalkWithDatParent());
        migrationBuilder.Sql(CreateArtworkCurrent("CREATE", "? 'DatParentMatch'"));
    }

    internal static string TrustWalkWithDatParent(string extraEvidenceCheck = "") =>
        TrustWalkSql(
            extraEvidenceCheck.Length == 0 ? ParentCheck : ParentCheck + "\n" + extraEvidenceCheck,
            """
              parent_match jsonb;
              parent_title integer;
              parent_revision uuid;
            """,
            "concat(title_id, ':', provider_id)");

    internal static string CreateArtworkCurrent(string create, string proofPredicate) => $$"""
            {{create}} FUNCTION romd.provider_identity_artwork_current(title_id integer, game_id text, policy_version text)
             RETURNS boolean
             LANGUAGE sql STABLE
            AS $function$
              SELECT NOT EXISTS (
                SELECT 1
                FROM romd."TitleProviderMatchStates" state
                JOIN romd."TitleExternalIds" link ON link."TitleId" = state."TitleId" AND link."Provider" = state."ProviderId"
                WHERE state."TitleId" = title_id AND state."ProviderId" = 'steamgriddb'
                  AND link."ExternalId" = game_id AND NOT link."IsConfirmed"
                  AND state."DecisionJson"::jsonb {{proofPredicate}}
                  AND NOT romd.provider_identity_trusted(title_id, 'steamgriddb', policy_version)
              );
            $function$;
            """;

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Older readers cannot validate the cross-title dependency.
        migrationBuilder.Sql("""
            UPDATE romd."ArtworkAssets" asset SET "IsEligible" = false
            FROM romd."TitleProviderMatchStates" state
            JOIN romd."TitleExternalIds" link ON link."TitleId" = state."TitleId" AND link."Provider" = state."ProviderId"
            WHERE asset."TitleId" = state."TitleId" AND asset."SourceId" = state."ProviderId"
              AND asset."ProviderGameId" = link."ExternalId" AND NOT link."IsConfirmed"
              AND state."DecisionJson"::jsonb ? 'DatParentMatch';
            UPDATE romd."TitleProviderMatchStates" SET "AutomaticallyTrusted" = false
            WHERE "DecisionJson"::jsonb ? 'DatParentMatch';
            DROP FUNCTION romd.provider_identity_artwork_current(integer, text, text);
            """);
        migrationBuilder.Sql(TrustWalkSql(string.Empty, string.Empty, "provider_id"));
        migrationBuilder.Sql("DROP FUNCTION romd.provider_identity_dat_parent_title(integer, integer);");
    }

    private const string ParentCheck = """
          parent_match := decision -> 'DatParentMatch';
          IF parent_match IS NOT NULL THEN
            IF provider_id <> 'steamgriddb' OR jsonb_typeof(parent_match) <> 'object'
              OR (parent_match ->> 'GameId') IS DISTINCT FROM identity_link."ExternalId" THEN RETURN false; END IF;
            parent_title := (parent_match ->> 'TitleId')::integer;
            IF parent_title IS NULL OR parent_title = title_id OR parent_title IS DISTINCT FROM
              romd.provider_identity_dat_parent_title((parent_match ->> 'DatFileId')::integer,
                (parent_match ->> 'DatGameId')::integer) THEN RETURN false; END IF;
            IF NOT EXISTS (SELECT FROM romd."TitleExternalIds" e
              WHERE e."TitleId" = parent_title AND e."Provider" = provider_id
                AND e."ExternalId" = parent_match ->> 'GameId') THEN RETURN false; END IF;
            SELECT COALESCE((SELECT s."Revision" FROM romd."TitleProviderMatchStates" s
              WHERE s."TitleId" = parent_title AND s."ProviderId" = provider_id),
              '00000000-0000-0000-0000-000000000000'::uuid) INTO parent_revision;
            IF parent_revision IS DISTINCT FROM (parent_match ->> 'Revision')::uuid
              OR NOT romd.provider_identity_trusted_walk(parent_title, provider_id, policy_version,
                array_append(visited, concat(title_id, ':', provider_id)), romd.provider_identity_fingerprint(parent_title, policy_version))
              THEN RETURN false; END IF;
          END IF;
        """;

    private static string TrustWalkSql(string parentCheck, string parentDeclarations, string visitKey) => $$"""
        CREATE OR REPLACE FUNCTION romd.provider_identity_trusted_walk(title_id integer, provider_id text, policy_version text, visited text[], current_fingerprint text)
         RETURNS boolean
         LANGUAGE plpgsql
         STABLE
        AS $function$
        DECLARE
          identity_link record;
          identity_state record;
          decision jsonb;
          dependency jsonb;
          dependency_revision uuid;
        {{parentDeclarations}}
        BEGIN
          IF {{visitKey}} = ANY(visited) THEN RETURN false; END IF;
          SELECT * INTO identity_link FROM romd."TitleExternalIds"
            WHERE "TitleId" = title_id AND "Provider" = provider_id;
          IF NOT FOUND THEN RETURN false; END IF;
          SELECT * INTO identity_state FROM romd."TitleProviderMatchStates"
            WHERE "TitleId" = title_id AND "ProviderId" = provider_id;
          IF identity_state."SuppressAutomaticMatch" IS TRUE THEN RETURN false; END IF;
          IF identity_link."IsConfirmed" THEN RETURN true; END IF;
          IF identity_state."AutomaticallyTrusted" IS NOT TRUE
            OR identity_state."MatcherVersion" IS DISTINCT FROM policy_version
            OR identity_state."InputFingerprint" IS DISTINCT FROM current_fingerprint
            OR (identity_state."GameJson"::jsonb ->> 'Id') IS DISTINCT FROM identity_link."ExternalId"
            OR identity_state."DecisionJson" IS NULL THEN RETURN false; END IF;
          decision := identity_state."DecisionJson"::jsonb;
          IF jsonb_typeof(decision) IS DISTINCT FROM 'object' THEN RETURN false; END IF;
        {{parentCheck}}
          IF decision -> 'Dependencies' IS NOT NULL AND decision -> 'Dependencies' <> 'null'::jsonb THEN
            IF jsonb_typeof(decision -> 'Dependencies') <> 'array' THEN RETURN false; END IF;
            FOR dependency IN SELECT value FROM jsonb_array_elements(decision -> 'Dependencies') LOOP
              IF NOT EXISTS (SELECT 1 FROM romd."TitleExternalIds" e
                WHERE e."TitleId" = title_id AND e."Provider" = dependency ->> 'ProviderId'
                  AND e."ExternalId" = dependency ->> 'GameId') THEN RETURN false; END IF;
              SELECT COALESCE((SELECT s."Revision" FROM romd."TitleProviderMatchStates" s
                WHERE s."TitleId" = title_id AND s."ProviderId" = dependency ->> 'ProviderId'),
                '00000000-0000-0000-0000-000000000000'::uuid) INTO dependency_revision;
              IF dependency_revision IS DISTINCT FROM (dependency ->> 'Revision')::uuid
                OR NOT romd.provider_identity_trusted_walk(title_id, dependency ->> 'ProviderId', policy_version,
                  array_append(visited, {{visitKey}}), current_fingerprint) THEN RETURN false; END IF;
            END LOOP;
          END IF;
          RETURN true;
        END;
        $function$;
        """;
}
