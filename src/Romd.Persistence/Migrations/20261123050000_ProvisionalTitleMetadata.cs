using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Romd.Persistence.Migrations;

[DbContext(typeof(RomdDbContext))]
[Migration("20261123050000_ProvisionalTitleMetadata")]
public sealed class ProvisionalTitleMetadata : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "TitleProvisionalMetadata",
            schema: "romd",
            columns: table => new
            {
                TitleId = table.Column<int>(type: "integer", nullable: false),
                ProviderId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                ProviderGameId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                MatchRevision = table.Column<Guid>(type: "uuid", nullable: false),
                MetadataJson = table.Column<string>(type: "jsonb", nullable: false),
                RatingsJson = table.Column<string>(type: "jsonb", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TitleProvisionalMetadata", x => x.TitleId);
                table.CheckConstraint("CK_TitleProvisionalMetadata_Provider", "(\"ProviderId\" = 'igdb')");
                table.ForeignKey(
                    name: "FK_TitleProvisionalMetadata_Titles_TitleId",
                    column: x => x.TitleId,
                    principalSchema: "romd",
                    principalTable: "Titles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.Sql("""
            CREATE FUNCTION romd.provider_provisional_metadata_game(
              title_id integer, provider_id text, policy_version text, provisional_policy_version text)
            RETURNS text
            LANGUAGE sql STABLE AS $function$
              SELECT decision.value ->> 'ProvisionalGameId'
              FROM romd."TitleProviderMatchStates" candidate
              CROSS JOIN LATERAL (
                SELECT CASE WHEN candidate."DecisionJson" IS JSON OBJECT THEN candidate."DecisionJson"::jsonb END AS value
              ) decision
              WHERE provider_id = 'igdb'
                AND candidate."TitleId" = title_id AND candidate."ProviderId" = provider_id
                AND NOT candidate."SuppressAutomaticMatch" AND NOT candidate."AutomaticallyTrusted"
                AND candidate."MatcherVersion" = policy_version
                AND candidate."InputFingerprint" = romd.provider_identity_fingerprint(title_id, policy_version)
                AND decision.value ->> 'State' = 'NeedsReview'
                AND decision.value ->> 'PosterPolicyVersion' = provisional_policy_version
                AND decision.value #>> '{Candidates,0,Game,Id}' = decision.value ->> 'ProvisionalGameId'
                AND EXISTS (SELECT FROM romd.provider_identity_dat_matches(title_id))
                AND NOT EXISTS (
                  SELECT FROM romd."TitleExternalIds" existing
                  WHERE existing."TitleId" = title_id AND lower(existing."Provider") = provider_id
                );
            $function$;

            CREATE FUNCTION romd.current_provisional_title_metadata(policy_version text, provisional_policy_version text)
            RETURNS SETOF romd."TitleProvisionalMetadata"
            LANGUAGE sql STABLE AS $function$
              SELECT metadata.*
              FROM romd."TitleProvisionalMetadata" metadata
              JOIN romd."TitleProviderMatchStates" current_match ON current_match."TitleId" = metadata."TitleId"
                AND current_match."ProviderId" = metadata."ProviderId" AND current_match."Revision" = metadata."MatchRevision"
              WHERE metadata."ProviderGameId" = romd.provider_provisional_metadata_game(
                metadata."TitleId", metadata."ProviderId", policy_version, provisional_policy_version);
            $function$;

            CREATE FUNCTION romd.presented_title_ratings(policy_version text, provisional_policy_version text)
            RETURNS TABLE (
              "TitleId" integer, "Board" integer, "Code" text, "Designation" integer, "MinimumAge" integer,
              "DescriptorsJson" text, "Synopsis" text, "SourceId" text, "ExternalRatingId" text)
            LANGUAGE sql STABLE AS $function$
              SELECT r."TitleId", r."Board", r."Code"::text, r."Designation", r."MinimumAge", r."DescriptorsJson",
                r."Synopsis"::text, r."SourceId"::text, r."ExternalRatingId"::text
              FROM romd."TitleContentRatings" r
              UNION ALL
              SELECT metadata."TitleId", rating."Board", rating."Code", rating."Designation", rating."MinimumAge",
                rating."DescriptorsJson", rating."Synopsis", rating."SourceId", rating."ExternalRatingId"
              FROM romd.current_provisional_title_metadata(policy_version, provisional_policy_version) metadata
              CROSS JOIN LATERAL jsonb_to_recordset(metadata."RatingsJson") AS rating (
                "Board" integer, "Code" text, "Designation" integer, "MinimumAge" integer,
                "DescriptorsJson" text, "Synopsis" text, "SourceId" text, "ExternalRatingId" text)
              WHERE NOT EXISTS (
                SELECT FROM romd."TitleContentRatings" published
                WHERE published."TitleId" = metadata."TitleId" AND published."Board" = rating."Board"
              );
            $function$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP FUNCTION romd.presented_title_ratings(text, text);
            DROP FUNCTION romd.current_provisional_title_metadata(text, text);
            DROP FUNCTION romd.provider_provisional_metadata_game(integer, text, text, text);
            """);
        migrationBuilder.DropTable("TitleProvisionalMetadata", "romd");
    }
}
