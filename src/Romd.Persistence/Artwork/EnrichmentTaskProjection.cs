using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Enrichment;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Artwork;
using Romd.Contracts.Management.Enrichment;
using Romd.Domain.Catalog;
using Romd.Persistence.Enrichment;
using Romd.Persistence.Entities;
namespace Romd.Persistence.Artwork;

/// <summary>
///     Run feedback over immutable execution history. Failed, Unfinished and Working read what the continuation leaf
///     recorded; Review reads the title's current assessment. Counts, pages and retry membership share each predicate.
/// </summary>
internal sealed class EnrichmentTaskProjection(RomdDbContext db, EnrichmentOptions? options = null)
{
    internal sealed record Summary(int Review, int Failed, int Unfinished, int Working, ArtworkRoleCount[] Roles);

    // The cursor restricts roots before traversal. The path prevents cycles and a missing follow-up leaves the
    // last existing item authoritative. Parameters are named so EF-generated assessment SQL can precede this text.
    private const string TraversalSql = """
        roots AS MATERIALIZED (
            SELECT * FROM romd."ArtworkCompletionItems"
            WHERE "JobId" = ANY(@romd_run_jobs) AND "TitleId" > @romd_run_after
            ORDER BY "JobId", "TitleId" LIMIT @romd_run_roots
        ), chain AS (
            SELECT r."JobId" AS root_job, r."TitleId" AS title_id, r."JobId" AS leaf_job,
                r."CompletedAt" AS completed_at, r."FollowUpJobId" AS follow_up, r."MetadataOutcome" AS metadata_outcome,
                ARRAY[r."JobId"] AS path, 0 AS depth
            FROM roots r
            UNION ALL
            SELECT c.root_job, c.title_id, i."JobId", i."CompletedAt", i."FollowUpJobId", i."MetadataOutcome",
                c.path || i."JobId", c.depth + 1
            FROM chain c JOIN romd."ArtworkCompletionItems" i
                ON i."JobId" = c.follow_up AND i."TitleId" = c.title_id
            WHERE NOT i."JobId" = ANY(c.path)
        ), leaves AS MATERIALIZED (
            SELECT DISTINCT ON (root_job, title_id) * FROM chain
            ORDER BY root_job, title_id, depth DESC
        )
        """;

    // Recorded predicates. A Working item's outcomes are provisional: it is never Failed and never retried.
    // Unknown (null) metadata history never counts as a failure. A removed title keeps its history in the
    // unfiltered list but is neither Failed nor Unfinished, so counts, lists and retry membership agree.
    private const string PredicatesSql = TraversalSql + """
        , items AS (
            SELECT r."JobId" AS root_job, r."TitleId" AS title_id, r."TitleName" AS title_name, r."FollowUpJobId" AS follow_up,
                l.leaf_job, l.completed_at, l.metadata_outcome, t."Id" IS NOT NULL AS present,
                l.completed_at IS NULL AND j."Phase" NOT IN ('Completed', 'CompletedWithErrors', 'Failed', 'Cancelled') AS working,
                t."Id" IS NOT NULL AND l.completed_at IS NULL AND j."Phase" IN ('Completed', 'CompletedWithErrors', 'Failed', 'Cancelled') AS unfinished
            FROM roots r JOIN leaves l ON l.root_job = r."JobId" AND l.title_id = r."TitleId"
            JOIN romd."Jobs" j ON j."Id" = l.leaf_job
            LEFT JOIN romd."Titles" t ON t."Id" = r."TitleId"
        ), predicates AS (
            SELECT i.*, i.present AND NOT i.working AND (i.metadata_outcome = 'Failed' OR EXISTS (
                SELECT 1 FROM romd."ArtworkCompletionResults" x
                WHERE x."JobId" = i.leaf_job AND x."TitleId" = i.title_id AND x."Outcome" = 'Failed')) AS failed
            FROM items i
        )
        """;

    // The only current predicate: the title is in the run and its assessment needs a decision now.
    // Statements that use it bind the facts summaries for the run's titles as the "now" relation. The
    // uncorrelated IN is one hashed subplan; a join here degrades to a nested loop over every facts row
    // because traversal row estimates collapse.
    private const string ReviewedSql = PredicatesSql + """
        , reviewed AS MATERIALIZED (
            SELECT p.*, p.title_id IN (SELECT n."TitleId" FROM now n WHERE n."NeedsReview") AS review FROM predicates p
        )
        """;

    // Recorded filters bind the closed status vocabulary as a parameter so every statement stays constant SQL.
    private const string RecordedFilterSql = """
        (@romd_run_status IS NULL OR @romd_run_status = 'Failed' AND p.failed
            OR @romd_run_status = 'Unfinished' AND p.unfinished OR @romd_run_status = 'Working' AND p.working)
        """;

    private const string PageColumnsSql = """
        SELECT m.title_id AS "TitleId", m.title_name AS "TitleName", m.follow_up AS "FollowUpJobId", m.working AS "Working",
            m.completed_at IS NOT NULL AS "Done", m.metadata_outcome AS "MetadataOutcome",
            COALESCE((SELECT jsonb_object_agg(x."Role", x."Outcome") FROM romd."ArtworkCompletionResults" x
                WHERE x."JobId" = m.leaf_job AND x."TitleId" = m.title_id), jsonb_build_object())::text AS "RolesJson"
        FROM matching m ORDER BY m.title_id
        """;

    private const string RecordedPageSql = "WITH RECURSIVE " + PredicatesSql + """
        , matching AS (
            SELECT * FROM predicates p WHERE
        """ + RecordedFilterSql + """
             ORDER BY p.title_id LIMIT @romd_run_limit
        )
        """ + PageColumnsSql;

    private const string ReviewPageSql = ReviewedSql + """
        , matching AS (
            SELECT * FROM reviewed p WHERE p.review ORDER BY p.title_id LIMIT @romd_run_limit
        )
        """ + PageColumnsSql;

    private const string RetryMembershipSql = "WITH RECURSIVE " + PredicatesSql + """
         SELECT p.title_id AS "Value" FROM predicates p WHERE
        """ + RecordedFilterSql;

    public Task AttachRetryAsync(Guid originalId, int[] titleIds, Guid retryId, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync("WITH RECURSIVE " + TraversalSql + """
            UPDATE romd."ArtworkCompletionItems" i SET "FollowUpJobId" = @romd_run_retry
            FROM leaves l WHERE i."JobId" = l.leaf_job AND i."TitleId" = l.title_id
                AND l.title_id = ANY(@romd_run_titles) AND i."FollowUpJobId" IS NULL
            """, [Parameter("romd_run_jobs", new[] { originalId }), Parameter("romd_run_after", 0),
            Parameter("romd_run_roots", int.MaxValue), Parameter("romd_run_titles", titleIds), Parameter("romd_run_retry", retryId)], ct);

    /// <summary>One bounded page. Only Review evaluates current facts over the run; rows carry Now for the page alone.</summary>
    public async Task<IReadOnlyList<ArtworkCompletionItem>> PageAsync(Guid id, int after, string? status,
        EnrichmentProviderAvailability providers, CancellationToken ct, int limit = 50)
    {
        if (status is not (null or "Review" or "Failed" or "Unfinished" or "Working"))
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported run filter.");
        var now = status == "Review" ? Assessments(db.Titles.Where(title => db.Set<ArtworkCompletionItemEntity>()
            .Any(item => item.JobId == id && item.TitleId == title.Id && item.TitleId > after)), providers) : null;
        DbParameter[] parameters = [Parameter("romd_run_jobs", new[] { id }), Parameter("romd_run_after", after),
            Parameter("romd_run_roots", status is null ? limit + 1 : int.MaxValue), Parameter("romd_run_limit", limit + 1)];
        var rows = now is null
            ? await db.Database.SqlQueryRaw<PageRow>(RecordedPageSql, [.. parameters, StatusParameter(status)]).ToListAsync(ct)
            : await ReadAsync(now, ReviewPageSql, reader => new PageRow
            {
                TitleId = reader.GetInt32(0),
                TitleName = reader.GetString(1),
                FollowUpJobId = reader.IsDBNull(2) ? null : reader.GetGuid(2),
                Working = reader.GetBoolean(3),
                Done = reader.GetBoolean(4),
                MetadataOutcome = reader.IsDBNull(5) ? null : reader.GetString(5),
                RolesJson = reader.GetString(6)
            }, ct, parameters);
        if (rows.Count == 0)
            return [];
        var titleIds = rows.Select(row => row.TitleId).ToArray();
        var current = await Assessments(db.Titles.Where(title => titleIds.Contains(title.Id)), providers).ToDictionaryAsync(x => x.TitleId, ct);
        return rows.Select(row => ToItem(row, current.GetValueOrDefault(row.TitleId))).ToArray();
    }

    /// <summary>Retry membership uses the recorded predicates only; Review has no retry.</summary>
    public Task<List<int>> MatchingTitleIdsAsync(Guid id, string status, CancellationToken ct) => status is "Failed" or "Unfinished"
        ? db.Database.SqlQueryRaw<int>(RetryMembershipSql, Parameter("romd_run_jobs", new[] { id }), Parameter("romd_run_after", 0),
            Parameter("romd_run_roots", int.MaxValue), StatusParameter(status)).ToListAsync(ct)
        : throw new ArgumentOutOfRangeException(nameof(status), status, "Only recorded outcomes can be retried.");

    /// <summary>All four counts and the recorded role totals of each run come from one statement and one snapshot.</summary>
    public async Task<IReadOnlyDictionary<Guid, Summary>> SummariesAsync(Guid[] ids, EnrichmentProviderAvailability providers, CancellationToken ct)
    {
        var now = Assessments(db.Titles.Where(title => db.Set<ArtworkCompletionItemEntity>()
            .Any(item => ids.Contains(item.JobId) && item.TitleId == title.Id)), providers);
        var rows = await ReadAsync(now, ReviewedSql + """
            SELECT p.root_job, NULL::text, NULL::text, 0,
                count(*) FILTER (WHERE p.review)::int, count(*) FILTER (WHERE p.failed)::int,
                count(*) FILTER (WHERE p.unfinished)::int, count(*) FILTER (WHERE p.working)::int
            FROM reviewed p GROUP BY p.root_job
            UNION ALL
            SELECT l.root_job, x."Role", x."Outcome", count(*)::int, 0, 0, 0, 0
            FROM leaves l JOIN romd."ArtworkCompletionResults" x ON x."JobId" = l.leaf_job AND x."TitleId" = l.title_id
            GROUP BY l.root_job, x."Role", x."Outcome"
            """, reader => new SummaryRow(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5),
                reader.GetInt32(6), reader.GetInt32(7)), ct,
            Parameter("romd_run_jobs", ids), Parameter("romd_run_after", 0), Parameter("romd_run_roots", int.MaxValue));
        return rows.GroupBy(x => x.JobId).ToDictionary(g => g.Key, g => new Summary(g.Sum(x => x.Review), g.Sum(x => x.Failed),
            g.Sum(x => x.Unfinished), g.Sum(x => x.Working),
            g.Where(x => x.Role is not null).OrderBy(x => x.Role).ThenBy(x => x.Outcome)
                .Select(x => new ArtworkRoleCount(x.Role!, x.Outcome!, x.Count)).ToArray()));
    }

    private IQueryable<EnrichmentAssessmentSummary> Assessments(IQueryable<TitleEntity> titles, EnrichmentProviderAvailability providers) =>
        new EnrichmentAssessmentQuery(db, titles, options, providers).Summaries();

    // Statements that need current facts bind EF's assessment command as the "now" relation.
    private Task<List<T>> ReadAsync<T>(IQueryable<EnrichmentAssessmentSummary> now, string sql, Func<DbDataReader, T> read,
        CancellationToken ct, params DbParameter[] parameters) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async attemptToken =>
        {
            // EF owns the assessment SQL and all its bound parameters. Compose only this trusted command around it,
            // never a diagnostic ToQueryString or user text; statuses are bound parameters, never SQL text.
            await using var command = now.CreateDbCommand();
            command.CommandText = $"WITH RECURSIVE now AS MATERIALIZED ({command.CommandText}), " + sql;
            command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
            command.Parameters.AddRange(parameters);
            var rows = new List<T>();
            await db.Database.OpenConnectionAsync(attemptToken);
            try
            {
                await using var reader = await command.ExecuteReaderAsync(attemptToken);
                while (await reader.ReadAsync(attemptToken))
                    rows.Add(read(reader));
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
            return rows;
        }, ct);

    private static Npgsql.NpgsqlParameter Parameter(string name, object value) => new(name, value);

    // Typed so a null status still binds as text.
    private static Npgsql.NpgsqlParameter StatusParameter(string? status) =>
        new("romd_run_status", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)status ?? DBNull.Value };

    private static ArtworkCompletionItem ToItem(PageRow row, EnrichmentAssessmentSummary? current) => new(
        IdCoder.Encode(row.TitleId), row.TitleName,
        // Execution state of the leaf; a removed title's unfinished item stays visible history outside the Unfinished filter.
        row.Working ? ArtworkCompletionState.Working : row.Done ? ArtworkCompletionState.Done : ArtworkCompletionState.Unfinished,
        JsonSerializer.Deserialize<Dictionary<ArtworkRole, string>>(row.RolesJson)!.OrderBy(x => x.Key)
            .Select(x => new ArtworkRoleCount(x.Key.ToString(), x.Value, 1)).ToArray(),
        row.MetadataOutcome switch
        {
            null => (ArtworkCompletionMetadataOutcome?)null,
            nameof(MetadataExecutionOutcome.Succeeded) => ArtworkCompletionMetadataOutcome.Succeeded,
            nameof(MetadataExecutionOutcome.Failed) => ArtworkCompletionMetadataOutcome.Failed,
            nameof(MetadataExecutionOutcome.NotAttempted) => ArtworkCompletionMetadataOutcome.NotAttempted,
            var unknown => throw new InvalidDataException($"Unknown recorded metadata outcome '{unknown}'.")
        },
        current is null ? null : new EnrichmentSummary(current.NeedsReview, current.CanEnrichAutomatically, current.HasActiveWork, current.HasOperationalBlocker),
        row.FollowUpJobId);

    private sealed class PageRow
    {
        public int TitleId { get; init; }
        public string TitleName { get; init; } = null!;
        public Guid? FollowUpJobId { get; init; }
        public bool Working { get; init; }
        public bool Done { get; init; }
        public string? MetadataOutcome { get; init; }
        public string RolesJson { get; init; } = null!;
    }

    private sealed record SummaryRow(Guid JobId, string? Role, string? Outcome, int Count, int Review, int Failed, int Unfinished, int Working);
}
