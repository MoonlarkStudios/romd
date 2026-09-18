using Microsoft.EntityFrameworkCore;
using Npgsql;
using Romd.Admin.Application.Titles;
using Romd.Domain.Catalog;
using Romd.Domain.Source.Dat;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Repositories;

public sealed class TrackedTitleRepository(RomdDbContext context, TimeProvider timeProvider)
    : ITrackedTitleRepository
{
    private const int BatchSize = 400;
    private const string Schema = PostgreSqlConfiguration.SchemaName;

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

        await context.Database.ExecuteSqlRawAsync(
            $"""
            INSERT INTO {Schema}."TrackedTitles" ("TitleId", "PinnedCatalogReleaseId", "CreatedAt", "UpdatedAt")
            VALUES (@titleId, @pinnedCatalogReleaseId, @now, @now)
            ON CONFLICT ("TitleId") DO UPDATE SET
                "PinnedCatalogReleaseId" = excluded."PinnedCatalogReleaseId",
                "UpdatedAt" = excluded."UpdatedAt";
            """,
            [
                new NpgsqlParameter("titleId", titleId),
                new NpgsqlParameter<int?>("pinnedCatalogReleaseId", pinnedCatalogReleaseId),
                NowParameter()
            ],
            cancellationToken);
        return TrackTitleResult.Updated;
    }

    public async Task<bool> UntrackAsync(int titleId, CancellationToken cancellationToken = default)
    {
        if (!await context.Titles.AnyAsync(t => t.Id == titleId, cancellationToken))
        {
            return false;
        }

        await context.TrackedTitles
            .Where(t => t.TitleId == titleId)
            .ExecuteDeleteAsync(cancellationToken);
        return true;
    }

    public Task<int> TrackByTitleIdsAsync(
        IReadOnlyCollection<int> titleIds,
        CancellationToken cancellationToken = default) =>
        TrackExplicitAsync(titleIds, cancellationToken);

    public async Task<int> UntrackByTitleIdsAsync(
        IReadOnlyCollection<int> titleIds,
        CancellationToken cancellationToken = default)
    {
        int affected = 0;
        foreach (var batch in titleIds.Distinct().Chunk(BatchSize))
        {
            affected += await context.TrackedTitles
                .Where(t => batch.Contains(t.TitleId))
                .ExecuteDeleteAsync(cancellationToken);
        }

        return affected;
    }

    public Task<int> TrackByPlatformAsync(int platformId, CancellationToken cancellationToken = default) =>
        context.Database.ExecuteSqlRawAsync(
            $"""
            INSERT INTO {Schema}."TrackedTitles" ("TitleId", "PinnedCatalogReleaseId", "CreatedAt", "UpdatedAt")
            SELECT title."Id", NULL::integer, @now, @now
            FROM {Schema}."Titles" title
            WHERE title."PlatformId" = @platformId
            ON CONFLICT ("TitleId") DO NOTHING;
            """,
            [new NpgsqlParameter("platformId", platformId), NowParameter()],
            cancellationToken);

    public Task<int> UntrackByPlatformAsync(int platformId, CancellationToken cancellationToken = default) =>
        context.TrackedTitles
            .Where(tracked => context.Titles.Any(t => t.Id == tracked.TitleId && t.PlatformId == platformId))
            .ExecuteDeleteAsync(cancellationToken);

    public Task<int> TrackByDatSourceAsync(int datSourceId, CancellationToken cancellationToken = default) =>
        context.Database.ExecuteSqlRawAsync(
            $"""
            INSERT INTO {Schema}."TrackedTitles" ("TitleId", "PinnedCatalogReleaseId", "CreatedAt", "UpdatedAt")
            SELECT DISTINCT link."TitleId", NULL::integer, @now, @now
            FROM {Schema}."TitleSourceLinks" link
            INNER JOIN {Schema}."DatGames" game ON game."SourceEntryId" = link."SourceEntryId"
            INNER JOIN {Schema}."DatFiles" dat ON dat."Id" = game."DatFileId"
            WHERE dat."DatSourceId" = @datSourceId
              AND dat."Lifecycle" = @lifecycle
            ON CONFLICT ("TitleId") DO NOTHING;
            """,
            [
                new NpgsqlParameter("datSourceId", datSourceId),
                new NpgsqlParameter("lifecycle", nameof(DatFileLifecycle.Active)),
                NowParameter()
            ],
            cancellationToken);

    public Task<int> UntrackByDatSourceAsync(int datSourceId, CancellationToken cancellationToken = default) =>
        context.TrackedTitles
            .Where(tracked => context.TitleSourceLinks.Any(link =>
                link.TitleId == tracked.TitleId && context.DatGames.Any(game =>
                    game.SourceEntryId == link.SourceEntryId && context.DatFiles.Any(dat =>
                        dat.Id == game.DatFileId
                        && dat.DatSourceId == datSourceId
                        && dat.Lifecycle == nameof(DatFileLifecycle.Active)))))
            .ExecuteDeleteAsync(cancellationToken);

    public async Task ConsolidateAsync(
        int sourceTitleId,
        int targetTitleId,
        CancellationToken cancellationToken = default)
    {
        await context.Database.ExecuteSqlRawAsync(
            $"""
            INSERT INTO {Schema}."TrackedTitles" ("TitleId", "PinnedCatalogReleaseId", "CreatedAt", "UpdatedAt")
            SELECT @targetTitleId, NULL::integer, @now, @now
            WHERE EXISTS (SELECT 1 FROM {Schema}."TrackedTitles" source WHERE source."TitleId" = @sourceTitleId)
              AND EXISTS (SELECT 1 FROM {Schema}."Titles" target WHERE target."Id" = @targetTitleId)
            ON CONFLICT ("TitleId") DO NOTHING;
            """,
            [
                new NpgsqlParameter("targetTitleId", targetTitleId),
                new NpgsqlParameter("sourceTitleId", sourceTitleId),
                NowParameter()
            ],
            cancellationToken);

        await context.TrackedTitles
            .Where(t => t.TitleId == sourceTitleId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private async Task<int> TrackExplicitAsync(
        IReadOnlyCollection<int> titleIds,
        CancellationToken cancellationToken)
    {
        int affected = 0;
        foreach (var batch in titleIds.Distinct().Chunk(BatchSize))
        {
            affected += await context.Database.ExecuteSqlRawAsync(
                $"""
                INSERT INTO {Schema}."TrackedTitles" ("TitleId", "PinnedCatalogReleaseId", "CreatedAt", "UpdatedAt")
                SELECT title."Id", NULL::integer, @now, @now
                FROM {Schema}."Titles" title
                WHERE title."Id" = ANY(@titleIds)
                ON CONFLICT ("TitleId") DO NOTHING;
                """,
                [new NpgsqlParameter("titleIds", batch), NowParameter()],
                cancellationToken);
        }

        return affected;
    }

    private NpgsqlParameter NowParameter() =>
        new("now", timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
}
