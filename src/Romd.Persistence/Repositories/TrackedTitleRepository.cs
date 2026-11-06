using Microsoft.EntityFrameworkCore;
using Npgsql;
using Romd.Admin.Application.Titles;
using Romd.Domain.Catalog;
using Romd.Domain.Source.Dat;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Repositories;

public sealed class TrackedTitleRepository(RomdDbContext context, TimeProvider timeProvider)
    : ITrackedTitleRepository, ITrackedTitleReader
{
    private const string Schema = PostgreSqlConfiguration.SchemaName;

    public Task<bool> HasTrackedAsync(IReadOnlyCollection<int>? titleIds = null, CancellationToken cancellationToken = default) =>
        titleIds is null
            ? context.TrackedTitles.AnyAsync(cancellationToken)
            : context.TrackedTitles.AnyAsync(t => titleIds.Contains(t.TitleId), cancellationToken);

    // RETURNING OLD is supported by ROMD's PostgreSQL 18 baseline. It distinguishes a
    // membership insertion from a pin-only upsert without a racy preliminary read.

    public async Task<TrackedTitle?> GetAsync(int titleId, CancellationToken cancellationToken = default)
    {
        var entity = await context.TrackedTitles
            .AsNoTracking()
            .SingleOrDefaultAsync(t => t.TitleId == titleId, cancellationToken);
        return entity?.ToDomain();
    }

    public async Task<TrackTitleResult> TrackAsync(
        int titleId,
        int? pinnedCatalogReleaseId = null,
        CancellationToken cancellationToken = default)
    {
        if (!await context.Titles.AnyAsync(t => t.Id == titleId, cancellationToken))
        {
            return TrackTitleResult.TitleNotFound;
        }

        if (pinnedCatalogReleaseId is int releaseId)
        {
            int? releaseTitleId = await context.CatalogReleases
                .Where(r => r.Id == releaseId)
                .Select(r => (int?)r.CatalogTitleId)
                .SingleOrDefaultAsync(cancellationToken);
            if (releaseTitleId is null)
            {
                return TrackTitleResult.PinnedCatalogReleaseNotFound;
            }

            if (releaseTitleId != titleId)
            {
                return TrackTitleResult.PinnedCatalogReleaseTitleMismatch;
            }
        }

        await MutateAsync(
            $"""
            INSERT INTO {Schema}."TrackedTitles" ("TitleId", "PinnedCatalogReleaseId", "CreatedAt", "UpdatedAt")
            VALUES (@titleId, @pin, @now, @now)
            ON CONFLICT ("TitleId") DO UPDATE SET
                "PinnedCatalogReleaseId" = excluded."PinnedCatalogReleaseId", "UpdatedAt" = excluded."UpdatedAt"
            RETURNING WITH (OLD AS previous) "TitleId", previous."TitleId" IS NULL AS "MembershipChanged"
            """,
            [new NpgsqlParameter("titleId", titleId), new NpgsqlParameter<int?>("pin", pinnedCatalogReleaseId)],
            cancellationToken);
        return TrackTitleResult.Updated;
    }

    public async Task<bool> UntrackAsync(int titleId, CancellationToken cancellationToken = default)
    {
        if (!await context.Titles.AnyAsync(t => t.Id == titleId, cancellationToken))
            return false;
        await UntrackByTitleIdsAsync([titleId], cancellationToken);
        return true;
    }

    public Task<int> TrackByTitleIdsAsync(IReadOnlyCollection<int> titleIds, CancellationToken cancellationToken = default) =>
        MutateExplicitAsync(titleIds, true, cancellationToken);

    public Task<int> UntrackByTitleIdsAsync(IReadOnlyCollection<int> titleIds, CancellationToken cancellationToken = default) =>
        MutateExplicitAsync(titleIds, false, cancellationToken);

    public Task<int> TrackByPlatformAsync(int platformId, CancellationToken cancellationToken = default) =>
        ChangeSelectionAsync(PlatformSelection, [new NpgsqlParameter("platformId", platformId)], true, cancellationToken);

    public Task<int> UntrackByPlatformAsync(int platformId, CancellationToken cancellationToken = default) =>
        ChangeSelectionAsync(PlatformSelection, [new NpgsqlParameter("platformId", platformId)], false, cancellationToken);

    public Task<int> TrackByDatSourceAsync(int datSourceId, CancellationToken cancellationToken = default) =>
        ChangeSelectionAsync(DatSourceSelection, SourceParameters(datSourceId), true, cancellationToken);

    public Task<int> UntrackByDatSourceAsync(int datSourceId, CancellationToken cancellationToken = default) =>
        ChangeSelectionAsync(DatSourceSelection, SourceParameters(datSourceId), false, cancellationToken);

    public async Task ConsolidateAsync(int sourceTitleId, int targetTitleId, CancellationToken cancellationToken = default)
    {
        await MutateAsync(
            $"""
            INSERT INTO {Schema}."TrackedTitles" ("TitleId", "PinnedCatalogReleaseId", "CreatedAt", "UpdatedAt")
            SELECT @targetTitleId, NULL::integer, @now, @now
            WHERE EXISTS (SELECT 1 FROM {Schema}."TrackedTitles" WHERE "TitleId" = @sourceTitleId)
              AND EXISTS (SELECT 1 FROM {Schema}."Titles" WHERE "Id" = @targetTitleId)
            ON CONFLICT ("TitleId") DO NOTHING
            RETURNING "TitleId", TRUE AS "MembershipChanged"
            """,
            [new NpgsqlParameter("targetTitleId", targetTitleId), new NpgsqlParameter("sourceTitleId", sourceTitleId)],
            cancellationToken,
            $"""DELETE FROM {Schema}."TrackedTitles" WHERE "TitleId" = @sourceTitleId RETURNING "TitleId", TRUE AS "MembershipChanged" """);
    }

    private Task<int> MutateExplicitAsync(IReadOnlyCollection<int> titleIds, bool tracked, CancellationToken ct) =>
        ChangeSelectionAsync(
            $"""SELECT "Id" FROM {Schema}."Titles" WHERE "Id" = ANY(@titleIds)""",
            [new NpgsqlParameter("titleIds", titleIds.Distinct().ToArray())], tracked, ct);

    private Task<int> ChangeSelectionAsync(string selection, NpgsqlParameter[] parameters, bool tracked, CancellationToken ct) =>
        MutateAsync(tracked
            ? $"""
              INSERT INTO {Schema}."TrackedTitles" ("TitleId", "PinnedCatalogReleaseId", "CreatedAt", "UpdatedAt")
              SELECT selected."Id", NULL::integer, @now, @now FROM ({selection}) selected ORDER BY selected."Id"
              ON CONFLICT ("TitleId") DO NOTHING RETURNING "TitleId", TRUE AS "MembershipChanged"
              """
            : $"""
              DELETE FROM {Schema}."TrackedTitles" WHERE "TitleId" IN ({selection}) RETURNING "TitleId", TRUE AS "MembershipChanged"
              """,
            parameters, ct, revokesMembership: !tracked);

    private Task<int> MutateAsync(string mutation, NpgsqlParameter[] parameters, CancellationToken ct, string? removal = null, bool revokesMembership = false) =>
        TrackingLibraryInvalidation.ApplyAsync(context, mutation, removal, parameters, timeProvider.GetUtcNow(), revokesMembership || removal is not null, ct);

    private static readonly string PlatformSelection =
        $"""SELECT "Id" FROM {Schema}."Titles" WHERE "PlatformId" = @platformId""";

    private static readonly string DatSourceSelection = $"""
        SELECT DISTINCT link."TitleId" AS "Id" FROM {Schema}."TitleSourceLinks" link
        JOIN {Schema}."DatGames" game ON game."SourceEntryId" = link."SourceEntryId"
        JOIN {Schema}."DatFiles" dat ON dat."Id" = game."DatFileId"
        WHERE dat."DatSourceId" = @datSourceId AND dat."Lifecycle" = @lifecycle
        """;

    private static NpgsqlParameter[] SourceParameters(int datSourceId) =>
        [new("datSourceId", datSourceId), new("lifecycle", nameof(DatFileLifecycle.Active))];
}
