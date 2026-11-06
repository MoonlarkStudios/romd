using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Romd.Persistence.Repositories;

/// <summary>
/// Marks tracking-dependent libraries for an explicit refresh in the
/// tracking transaction. SQL evaluates the current configuration while locking the library,
/// avoiding a configuration-read/update race and one round trip per changed title/library.
/// </summary>
internal static class TrackingLibraryInvalidation
{
    public static async Task<int> ApplyAsync(RomdDbContext context, string mutation, string? removal,
        NpgsqlParameter[] parameters, DateTimeOffset now, bool revokesMembership, CancellationToken ct)
    {
        // All SQL fragments come from this persistence implementation; values are parameters.
        // A single statement is atomic both standalone and inside an ingestion/merge transaction.
        var changes = removal is null
            ? $"WITH changed AS ({mutation}),"
            : $"WITH inserted AS ({mutation}), removed AS ({removal}), " +
              "changed AS (SELECT * FROM inserted UNION ALL SELECT * FROM removed),";
        var sql = changes +
            """
            affected AS MATERIALIZED (
                SELECT library."Id"
                FROM romd."Libraries" library
                CROSS JOIN LATERAL (
                    SELECT CASE WHEN pg_input_is_valid(library."ConfigurationJson", 'jsonb')
                        THEN library."ConfigurationJson"::jsonb ELSE '{{}}'::jsonb END AS config
                ) policy
                WHERE library."ConfigurationState" = 'Valid'
                  AND NOT policy.config @> '{{"IncludeUntrackedTitles":true}}'::jsonb
                  AND EXISTS (
                      SELECT 1 FROM romd."Titles" title
                      WHERE title."Id" IN (SELECT "TitleId" FROM changed WHERE "MembershipChanged")
                        AND NOT COALESCE(policy.config->'ExcludeTitleIds', '[]'::jsonb) @> jsonb_build_array(title."Id")
                        AND CASE WHEN policy.config->'TitleSelectionMode' = '1'::jsonb
                            THEN COALESCE(policy.config->'IncludeTitleIds', '[]'::jsonb) @> jsonb_build_array(title."Id")
                            ELSE COALESCE(policy.config->'AllowedPlatformIds', '[]'::jsonb) = '[]'::jsonb
                                OR policy.config->'AllowedPlatformIds' @> jsonb_build_array(title."PlatformId")
                            END
                  )
                ORDER BY library."Id"
                FOR UPDATE OF library
            ), invalidated AS (
                UPDATE romd."Libraries" library
                SET "NeedsMaterialization" = library."NeedsMaterialization" OR @revokesMembership,
                    "MaterializationRevision" = gen_random_uuid(),
                    "UpdatedAt" = GREATEST(@now, library."LastMaterializedAt" + 1)
                FROM affected WHERE library."Id" = affected."Id"
            )
            SELECT count(*)::integer AS "Value" FROM changed WHERE "MembershipChanged";
            """;
        var counts = await context.Database.SqlQueryRaw<int>(sql,
            [.. parameters.Cast<object>(), new NpgsqlParameter("now", now.ToUnixTimeMilliseconds()), new NpgsqlParameter("revokesMembership", revokesMembership)]).ToListAsync(ct);
        return counts.Single();
    }
}
