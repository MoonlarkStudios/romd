using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.Pagination;
using Romd.Consumer.Application.Browse.ReadModels;
using Romd.Consumer.Application.Libraries;

namespace Romd.Persistence.Repositories;

public sealed partial class ConsumerBrowseRepository
{
    public Task<ConsumerLibraryReadResult<PagedList<ConsumerMediaData>>> ListTitleMediaAsync(
        ConsumerLibraryScope scope, int titleId, string? cursor, int limit, CancellationToken ct = default) =>
        _liveConsumerLibrary.ReadAsync<PagedList<ConsumerMediaData>>(scope,
            (libraryId, token) => ReadTitleMediaPageForLibraryAsync(scope.UserId, libraryId, titleId, cursor, limit, token), ct);

    public Task<ConsumerLibraryReadResult<PagedList<ConsumerReleaseData>>> ListTitleReleasesAsync(
        ConsumerLibraryScope scope, int titleId, string? cursor, int limit, CancellationToken ct = default) =>
        _liveConsumerLibrary.ReadAsync<PagedList<ConsumerReleaseData>>(scope,
            (libraryId, token) => ReadTitleReleasesPageForLibraryAsync(scope.UserId, libraryId, titleId, cursor, limit, token), ct);

    private async Task<ConsumerLibraryProjectionResult<PagedList<ConsumerMediaData>>> ReadTitleMediaPageForLibraryAsync(
        Guid userId, int libraryId, int titleId, string? cursor, int limit, CancellationToken ct)
    {
        if (!await OwnedMaterializedLibraryTitles(libraryId).AnyAsync(item => item.TitleId == titleId, ct))
            return new ConsumerLibraryProjectionResult<PagedList<ConsumerMediaData>>.ItemNotFound();
        string fingerprint = KeysetCursor.Fingerprint("consumer.title-media", userId, libraryId, titleId);
        var decoded = KeysetCursor.Decode<MediaPosition>(cursor, fingerprint,
            value => value.Id > 0 && !string.IsNullOrEmpty(value.Type));
        if (decoded.IsError)
            return new ConsumerLibraryProjectionResult<PagedList<ConsumerMediaData>>.Rejected(decoded.FirstError);
        var query = context.TitleMedia.AsNoTracking().Where(item => item.TitleId == titleId);
        if (decoded.Value is { } position)
            query = query.Where(item => item.Type.CompareTo(position.Type) > 0 || item.Type == position.Type
                && (position.IsPrimary && !item.IsPrimary || item.IsPrimary == position.IsPrimary && item.Id > position.Id));
        var rows = await query.OrderBy(item => item.Type).ThenByDescending(item => item.IsPrimary).ThenBy(item => item.Id)
            .Select(item => new ConsumerMediaData { Id = item.Id, Type = item.Type, IsPrimary = item.IsPrimary })
            .Take(limit + 1).ToListAsync(ct);
        bool more = rows.Count > limit;
        var items = rows.Take(limit).ToArray();
        return new ConsumerLibraryProjectionResult<PagedList<ConsumerMediaData>>.Found(new(items,
            more ? KeysetCursor.Encode(new MediaPosition(items[^1].Type, items[^1].IsPrimary, items[^1].Id), fingerprint) : null, more));
    }

    private async Task<ConsumerLibraryProjectionResult<PagedList<ConsumerReleaseData>>> ReadTitleReleasesPageForLibraryAsync(
        Guid userId, int libraryId, int titleId, string? cursor, int limit, CancellationToken ct)
    {
        if (!await OwnedMaterializedLibraryTitles(libraryId).AnyAsync(item => item.TitleId == titleId, ct))
            return new ConsumerLibraryProjectionResult<PagedList<ConsumerReleaseData>>.ItemNotFound();
        string fingerprint = KeysetCursor.Fingerprint("consumer.title-releases", userId, libraryId, titleId);
        var decoded = KeysetCursor.Decode<IntegerPosition>(cursor, fingerprint, value => value.IsValid);
        if (decoded.IsError)
            return new ConsumerLibraryProjectionResult<PagedList<ConsumerReleaseData>>.Rejected(decoded.FirstError);
        int after = decoded.Value?.Id ?? 0;
        var releaseIds = await (from projection in context.MaterializedLibraryReleases.AsNoTracking()
                                join release in context.CatalogReleases.AsNoTracking() on projection.CatalogReleaseId equals release.Id
                                where projection.LibraryId == libraryId && projection.TitleId == titleId && projection.IsOwned
                                    && projection.IsExposed && release.CatalogTitleId == titleId && release.Id > after
                                select release.Id).Distinct().Order().Take(limit + 1).ToArrayAsync(ct);
        bool more = releaseIds.Length > limit;
        var ids = releaseIds.Take(limit).ToArray();
        var candidates = await ConsumerReleaseProjection.LoadByTitleIdsAsync(context, libraryId, [titleId], ct, ids);
        var items = candidates.GetValueOrDefault(titleId, []).Select(item => item.Release).OrderBy(item => item.Id).ToArray();
        return new ConsumerLibraryProjectionResult<PagedList<ConsumerReleaseData>>.Found(new(items,
            more ? KeysetCursor.Encode(new IntegerPosition(ids[^1]), fingerprint) : null, more));
    }

    private sealed record MediaPosition(string Type, bool IsPrimary, int Id);
}
