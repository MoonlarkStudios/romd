using Romd.Persistence.Queries;
using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.Artwork;
using Romd.Application.Common.Pagination;
using Romd.Consumer.Application.Activity;
using Romd.Consumer.Application.Libraries;
using Romd.Domain.Activity;
using Romd.Domain.Libraries;
using Romd.Persistence.Entities;

namespace Romd.Persistence.Repositories;

public sealed class PlayActivityRepository(
    RomdDbContext context,
    TimeProvider timeProvider,
    IArtworkReader artworkReader) : IPlayActivityRepository
{
    private static readonly string ValidLibraryState = LibraryConfigurationState.Valid.ToString();

    public async Task<PlaySessionUpsertResult> UpsertAsync(
        Guid userId,
        Guid sessionId,
        PlaySessionSnapshot snapshot,
        CancellationToken ct = default)
    {
        var entity = await context.PlaySessions.AsTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId && item.SessionId == sessionId, ct);
        var now = timeProvider.GetUtcNow();
        if (entity is null)
        {
            var created = new PlaySession(
                userId, sessionId, snapshot.ClientId, snapshot.TitleId, snapshot.ReleaseId,
                snapshot.StartedAt, snapshot.EndedAt, snapshot.ActiveDurationSeconds, now, now);
            context.PlaySessions.Add(PlaySessionEntity.FromDomain(created));
            return new PlaySessionUpsertResult(PlaySessionUpsertStatus.Created, created);
        }

        var session = entity.ToDomain();
        // Domain comparisons remain exact. A database readback has Unix-millisecond precision,
        // so align matching persisted facts without truncating a newly supplied ending.
        var comparison = snapshot with
        {
            StartedAt = snapshot.StartedAt.ToUnixTimeMilliseconds() == session.StartedAt.ToUnixTimeMilliseconds()
                ? session.StartedAt : snapshot.StartedAt,
            EndedAt = snapshot.EndedAt is { } endedAt && session.EndedAt is { } storedEnd
                && endedAt.ToUnixTimeMilliseconds() == storedEnd.ToUnixTimeMilliseconds()
                ? storedEnd : snapshot.EndedAt
        };
        var merge = session.Merge(comparison, now);
        if (merge == PlaySessionMergeResult.Conflict)
        {
            return new PlaySessionUpsertResult(PlaySessionUpsertStatus.Conflict, null);
        }

        if (merge == PlaySessionMergeResult.Updated)
        {
            entity.EndedAt = session.EndedAt;
            entity.ActiveDurationSeconds = session.ActiveDurationSeconds;
            entity.UpdatedAt = session.UpdatedAt;
        }

        return new PlaySessionUpsertResult(
            merge == PlaySessionMergeResult.Updated ? PlaySessionUpsertStatus.Updated : PlaySessionUpsertStatus.Unchanged,
            session);
    }

    public async Task<PlaySession?> GetAccessibleAsync(Guid userId, Guid sessionId, CancellationToken ct = default)
    {
        int? libraryId = await GetLiveLibraryIdAsync(userId, ct);
        if (libraryId is null)
        {
            return null;
        }

        var entity = await AccessibleSessions(userId, libraryId.Value)
            .SingleOrDefaultAsync(item => item.SessionId == sessionId, ct);
        return entity?.ToDomain();
    }

    public async Task<PagedList<PlaySession>?> ListAccessibleAsync(
        Guid userId,
        string? cursor,
        int limit,
        CancellationToken ct = default)
    {
        var result = await new LiveConsumerLibraryQuery(context).ReadAsync<PagedList<PlaySession>>(new(userId),
            (libraryId, token) => ReadSessionsForLibraryAsync(userId, libraryId, cursor, limit, token), ct);
        return result switch
        {
            ConsumerLibraryReadResult<PagedList<PlaySession>>.Found found => found.Value,
            ConsumerLibraryReadResult<PagedList<PlaySession>>.Rejected => null,
            _ => new PagedList<PlaySession>([], null, false)
        };
    }

    private async Task<ConsumerLibraryProjectionResult<PagedList<PlaySession>>> ReadSessionsForLibraryAsync(
        Guid userId, int libraryId, string? cursor, int limit, CancellationToken ct)
    {
        string fingerprint = KeysetCursor.Fingerprint("consumer.play-sessions", userId, libraryId);
        var decoded = KeysetCursor.Decode<PlaySessionCursor>(cursor, fingerprint,
            value => value.StartedAt != default && value.SessionId != Guid.Empty);
        if (decoded.IsError)
            return new ConsumerLibraryProjectionResult<PagedList<PlaySession>>.Rejected(decoded.FirstError);
        var parsedCursor = decoded.Value;

        var query = AccessibleSessions(userId, libraryId);
        if (parsedCursor is not null)
        {
            query = query.Where(item =>
                item.StartedAt < parsedCursor.StartedAt ||
                item.StartedAt == parsedCursor.StartedAt && item.SessionId.CompareTo(parsedCursor.SessionId) < 0);
        }

        var rows = await query
            .OrderByDescending(item => item.StartedAt)
            .ThenByDescending(item => item.SessionId)
            .Take(limit + 1)
            .ToListAsync(ct);
        bool hasNext = rows.Count > limit;
        if (hasNext)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        string? next = hasNext && rows.Count > 0
            ? KeysetCursor.Encode(new PlaySessionCursor(rows[^1].StartedAt, rows[^1].SessionId), fingerprint)
            : null;
        return new ConsumerLibraryProjectionResult<PagedList<PlaySession>>.Found(new(rows.Select(item => item.ToDomain()).ToArray(), next, hasNext));
    }

    public async Task<IReadOnlyList<RecentlyPlayedData>?> ListRecentlyPlayedAsync(
        Guid userId,
        int limit,
        CancellationToken ct = default)
    {
        int? libraryId = await GetLiveLibraryIdAsync(userId, ct);
        if (libraryId is null)
        {
            return null;
        }

        return await ReadRecentlyPlayedForLibraryAsync(userId, libraryId.Value, limit, null, ct);
    }

    private async Task<IReadOnlyList<RecentlyPlayedData>> ReadRecentlyPlayedForLibraryAsync(
        Guid userId, int libraryId, int limit, RecentPosition? position, CancellationToken ct)
    {
        var activity = AccessibleSessions(userId, libraryId)
            .GroupBy(item => item.TitleId)
            .Select(group => new
            {
                TitleId = group.Key,
                LastPlayedAt = group.Max(item => item.EndedAt ?? item.StartedAt),
                PlayCount = group.Count()
            });
        if (position is not null)
            activity = activity.Where(item => item.LastPlayedAt < position.LastPlayedAt
                || item.LastPlayedAt == position.LastPlayedAt && item.TitleId < position.TitleId);
        var rows = await (
                from recent in activity
                join title in context.Titles on recent.TitleId equals title.Id
                join platform in context.Platforms on title.PlatformId equals platform.Id
                orderby recent.LastPlayedAt descending, recent.TitleId descending
                select new
                {
                    title.Id,
                    title.PlatformId,
                    PlatformName = platform.Name,
                    title.Name,
                    title.Genre,
                    title.ReleaseDate,
                    title.Rating,
                    recent.LastPlayedAt,
                    recent.PlayCount,
                    CoverMediaId = context.TitleMedia
                        .Where(media => media.TitleId == title.Id && media.Type == "Cover")
                        .OrderByDescending(media => media.IsPrimary)
                        .ThenBy(media => media.Id)
                        .Select(media => (int?)media.Id)
                        .FirstOrDefault(),
                    ReleaseCount = context.MaterializedLibraryReleases.Count(release =>
                        release.LibraryId == libraryId && release.TitleId == title.Id &&
                        release.IsOwned && release.IsExposed && release.CatalogReleaseId != null),
                    DefaultReleaseId = context.MaterializedLibraryReleases
                        .Where(release => release.LibraryId == libraryId && release.TitleId == title.Id &&
                            release.IsOwned && release.IsExposed && release.CatalogReleaseId != null)
                        .OrderBy(release => release.CatalogReleaseId)
                        .Select(release => release.CatalogReleaseId)
                        .FirstOrDefault()
                })
            .Take(limit)
            .ToListAsync(ct);

        var artwork = await artworkReader.ResolveAsync(rows.Select(item => item.Id).ToArray(), ct);
        var systems = await new SystemSummaryReader(context).ReadAsync(rows.Select(x => x.PlatformId), ct);
        return rows.Select(item => new RecentlyPlayedData(
            item.Id,
            item.PlatformId,
            systems[item.PlatformId],
            item.Name,
            item.CoverMediaId,
            item.Genre,
            item.ReleaseDate,
            item.Rating,
            item.ReleaseCount,
            item.DefaultReleaseId,
            item.LastPlayedAt,
            item.PlayCount,
            artwork.GetValueOrDefault(item.Id, []))).ToArray();
    }

    public Task<ConsumerLibraryReadResult<PagedList<RecentlyPlayedData>>> ListRecentlyPlayedPageAsync(
        Guid userId, string? cursor, int limit, CancellationToken ct = default) =>
        new LiveConsumerLibraryQuery(context).ReadAsync<PagedList<RecentlyPlayedData>>(new(userId), async (libraryId, token) =>
        {
            string fingerprint = KeysetCursor.Fingerprint("consumer.recently-played", userId, libraryId);
            var decoded = KeysetCursor.Decode<RecentPosition>(cursor, fingerprint,
                value => value.LastPlayedAt != default && value.TitleId > 0);
            if (decoded.IsError)
                return new ConsumerLibraryProjectionResult<PagedList<RecentlyPlayedData>>.Rejected(decoded.FirstError);
            var rows = await ReadRecentlyPlayedForLibraryAsync(userId, libraryId, limit + 1, decoded.Value, token);
            bool more = rows.Count > limit;
            var items = rows.Take(limit).ToArray();
            return new ConsumerLibraryProjectionResult<PagedList<RecentlyPlayedData>>.Found(new(items,
                more ? KeysetCursor.Encode(new RecentPosition(items[^1].LastPlayedAt, items[^1].TitleId), fingerprint) : null, more));
        }, ct);

    private sealed record RecentPosition(DateTimeOffset LastPlayedAt, int TitleId);

    public async Task DeleteAsync(Guid userId, Guid sessionId, CancellationToken ct = default)
    {
        await context.PlaySessions
            .Where(item => item.UserId == userId && item.SessionId == sessionId)
            .ExecuteDeleteAsync(ct);
    }

    public async Task ClearAsync(Guid userId, CancellationToken ct = default)
    {
        await context.PlaySessions.Where(item => item.UserId == userId).ExecuteDeleteAsync(ct);
    }

    private IQueryable<PlaySessionEntity> AccessibleSessions(Guid userId, int libraryId) =>
        context.PlaySessions.AsNoTracking().Where(session =>
            session.UserId == userId &&
            context.MaterializedLibraryTitles.Any(title =>
                title.LibraryId == libraryId && title.TitleId == session.TitleId &&
                title.IsOwned && title.IsVisible) &&
            context.MaterializedLibraryReleases.Any(release =>
                release.LibraryId == libraryId && release.TitleId == session.TitleId &&
                release.CatalogReleaseId == session.ReleaseId && release.IsOwned && release.IsExposed));

    private Task<int?> GetLiveLibraryIdAsync(Guid userId, CancellationToken ct) =>
        (from user in context.Users.AsNoTracking()
         where user.Id == userId && user.LibraryId != null
         join library in context.Libraries.AsNoTracking() on user.LibraryId equals library.Id
         where !(library.NeedsMaterialization && library.LastMaterializedAt == null) &&
               library.ConfigurationState == ValidLibraryState
         select (int?)library.Id).SingleOrDefaultAsync(ct);

    private sealed record PlaySessionCursor(DateTimeOffset StartedAt, Guid SessionId);
}
