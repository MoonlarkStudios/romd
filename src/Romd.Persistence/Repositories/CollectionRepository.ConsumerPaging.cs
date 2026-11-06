using Microsoft.EntityFrameworkCore;
using Romd.Application.Common.Pagination;
using Romd.Application.Common.Systems;
using Romd.Consumer.Application.Browse;
using Romd.Consumer.Application.Browse.ReadModels;
using Romd.Consumer.Application.Collections;
using Romd.Consumer.Application.Libraries;
using Romd.Persistence.Entities;
using Romd.Persistence.Queries;

namespace Romd.Persistence.Repositories;

public sealed partial class CollectionRepository
{
    public Task<ConsumerLibraryReadResult<PagedList<ConsumerCollectionReadModel>>> GetCollectionsPageAsync(
        ConsumerLibraryScope scope, int? platformId, string? cursor, int limit, CancellationToken ct = default) =>
        _liveConsumerLibrary.ReadAsync<PagedList<ConsumerCollectionReadModel>>(scope, async (libraryId, token) =>
        {
            string fingerprint = KeysetCursor.Fingerprint("consumer.collections", scope.UserId, libraryId, platformId);
            var decoded = KeysetCursor.Decode<ConsumerCollectionPosition>(cursor, fingerprint,
                value => value.Id > 0 && value.Name is not null);
            if (decoded.IsError)
                return new ConsumerLibraryProjectionResult<PagedList<ConsumerCollectionReadModel>>.Rejected(decoded.FirstError);
            var query = from placement in context.LibraryCollections.AsNoTracking()
                        join collection in context.Collections.AsNoTracking() on placement.CollectionId equals collection.Id
                        where placement.LibraryId == libraryId
                            && (platformId == null || collection.PlatformId == platformId || collection.PlatformId == null)
                            && context.CollectionItems.Any(item => item.CollectionId == collection.Id
                                && OwnedMaterializedLibraryTitles(libraryId).Any(title => title.TitleId == item.TitleId))
                        select new { Collection = collection, Placement = placement };
            if (decoded.Value is { } position)
                query = query.Where(item => item.Placement.SortOrder > position.SortOrder
                    || item.Placement.SortOrder == position.SortOrder && (item.Collection.Name.CompareTo(position.Name) > 0
                        || item.Collection.Name == position.Name && item.Collection.Id > position.Id));
            var rows = await query.OrderBy(item => item.Placement.SortOrder).ThenBy(item => item.Collection.Name).ThenBy(item => item.Collection.Id)
                .Select(item => new
                {
                    item.Collection.Id,
                    item.Collection.Name,
                    item.Collection.Description,
                    item.Collection.CoverMediaId,
                    item.Collection.PlatformId,
                    item.Placement.SortOrder,
                    item.Placement.IsFeatured,
                    ItemCount = context.CollectionItems.Where(member => member.CollectionId == item.Collection.Id
                        && OwnedMaterializedLibraryTitles(libraryId).Any(title => title.TitleId == member.TitleId))
                        .Select(member => member.TitleId).Distinct().Count()
                })
                .Take(limit + 1).ToListAsync(token);
            bool more = rows.Count > limit;
            rows = rows.Take(limit).ToList();
            var systems = await new SystemSummaryReader(context).ReadAsync(rows.Where(x => x.PlatformId.HasValue).Select(x => x.PlatformId!.Value), token);
            var items = rows.Select(x => new ConsumerCollectionReadModel(x.Id, x.Name, x.Description,
                x.CoverMediaId, x.PlatformId, x.PlatformId is { } id ? systems[id] : null, x.ItemCount, x.IsFeatured)
            { SortOrder = x.SortOrder }).ToArray();
            string? next = more ? KeysetCursor.Encode(new ConsumerCollectionPosition(rows[^1].SortOrder, rows[^1].Name, rows[^1].Id), fingerprint) : null;
            return new ConsumerLibraryProjectionResult<PagedList<ConsumerCollectionReadModel>>.Found(new(items, next, more));
        }, ct);

    public Task<ConsumerLibraryReadResult<PagedList<ConsumerCollectionTitleReadModel>>> GetCollectionTitlesPageAsync(
        ConsumerLibraryScope scope, int collectionId, string? cursor, ConsumerReleasePreference releasePreference,
        int limit, CancellationToken ct = default) =>
        _liveConsumerLibrary.ReadAsync<PagedList<ConsumerCollectionTitleReadModel>>(scope, async (libraryId, token) =>
        {
            string fingerprint = KeysetCursor.Fingerprint("consumer.collection-titles", scope.UserId, libraryId, collectionId);
            var decoded = KeysetCursor.Decode<ConsumerCollectionTitleCursor>(cursor, fingerprint, value => value.TitleId > 0);
            if (decoded.IsError)
                return new ConsumerLibraryProjectionResult<PagedList<ConsumerCollectionTitleReadModel>>.Rejected(decoded.FirstError);
            var result = await GetCollectionTitlesForLibraryAsync(libraryId, collectionId, decoded.Value, releasePreference, limit + 1, token);
            if (result is not ConsumerLibraryProjectionResult<IReadOnlyList<ConsumerCollectionTitleReadModel>>.Found found)
                return new ConsumerLibraryProjectionResult<PagedList<ConsumerCollectionTitleReadModel>>.ItemNotFound();
            bool more = found.Value.Count > limit;
            var rows = found.Value.Take(limit).ToArray();
            string? next = more ? KeysetCursor.Encode(new ConsumerCollectionTitleCursor(rows[^1].SortOrder, rows[^1].TitleId), fingerprint) : null;
            return new ConsumerLibraryProjectionResult<PagedList<ConsumerCollectionTitleReadModel>>.Found(new(rows, next, more));
        }, ct);

    private sealed record ConsumerCollectionPosition(int SortOrder, string Name, int Id);
    private async Task<ConsumerLibraryProjectionResult<IReadOnlyList<ConsumerCollectionTitleReadModel>>>
        GetCollectionTitlesForLibraryAsync(
        int libraryId,
        int collectionId,
        ConsumerCollectionTitleCursor? cursor,
        ConsumerReleasePreference releasePreference,
        int limit,
        CancellationToken ct)
    {
        bool collectionExists = await GetConsumerCollectionSummaries(
                libraryId,
                collectionId: collectionId)
            .AnyAsync(ct);
        if (!collectionExists)
        {
            return new ConsumerLibraryProjectionResult<IReadOnlyList<ConsumerCollectionTitleReadModel>>.ItemNotFound();
        }

        var query = context.CollectionItems
            .AsNoTracking()
            .Where(i => i.CollectionId == collectionId)
            .Where(i => OwnedMaterializedLibraryTitles(libraryId).Any(title => title.TitleId == i.TitleId))
            .Join(
                context.Titles.AsNoTracking(),
                i => i.TitleId,
                t => t.Id,
                (i, t) => new { Item = i, Title = t })
            .Join(
                context.Platforms.AsNoTracking(),
                x => x.Title.PlatformId,
                p => p.Id,
                (x, p) => new { x.Item, x.Title, Platform = p });

        if (cursor is not null)
        {
            query = query.Where(x =>
                x.Item.SortOrder > cursor.SortOrder
                || (x.Item.SortOrder == cursor.SortOrder && x.Item.TitleId > cursor.TitleId));
        }

        var titleRows = await query
            .OrderBy(x => x.Item.SortOrder)
            .ThenBy(x => x.Item.TitleId)
            .Select(x => new ConsumerCollectionTitleReadModel(
                x.Item.TitleId,
                x.Title.Name,
                x.Title.PlatformId,
                new SystemSummaryData(x.Platform.CanonicalKey ?? x.Platform.ShortName, x.Platform.Name, x.Platform.Name, null),
                x.Title.Media
                    .Where(m => m.Type == "Cover" && m.IsPrimary)
                    .Select(m => (int?)m.Id)
                    .FirstOrDefault(),
                x.Title.Genre,
                x.Title.ReleaseDate,
                x.Title.Rating,
                0,
                null,
                x.Item.SortOrder))
            .Take(limit)
            .ToListAsync(ct);

        var releaseCandidatesByTitleId = await ConsumerReleaseProjection.LoadByTitleIdsAsync(
            context,
            libraryId,
            titleRows.Select(item => item.TitleId).ToList(),
            ct);

        var artwork = await new ArtworkReader(context).ResolveAsync(
            titleRows.Select(item => item.TitleId).ToArray(), ct);

        var systems = await new SystemSummaryReader(context).ReadAsync(titleRows.Select(x => x.PlatformId), ct);

        return new ConsumerLibraryProjectionResult<IReadOnlyList<ConsumerCollectionTitleReadModel>>.Found(
            titleRows
                .Select(item => AddReleaseSummary(item, releaseCandidatesByTitleId, releasePreference) with
                {
                    Artwork = artwork[item.TitleId],
                    System = systems[item.PlatformId]
                })
                .ToList());
    }


    private IQueryable<ConsumerCollectionReadModel> GetConsumerCollectionSummaries(
        int libraryId,
        int? platformId = null,
        int? collectionId = null)
    {
        var ownedCollectionItemCounts = context.CollectionItems
            .AsNoTracking()
            .Join(
                OwnedMaterializedLibraryTitles(libraryId),
                ci => ci.TitleId,
                title => title.TitleId,
                (ci, _) => new { ci.CollectionId, ci.TitleId })
            .Distinct()
            .GroupBy(item => item.CollectionId)
            .Select(group => new { CollectionId = group.Key, ItemCount = group.Count() });

        var query = context.Collections
            .AsNoTracking()
            .Where(c => context.LibraryCollections.Any(a => a.LibraryId == libraryId && a.CollectionId == c.Id))
            .Join(
                ownedCollectionItemCounts,
                collection => collection.Id,
                count => count.CollectionId,
                (collection, count) => new { Collection = collection, count.ItemCount })
            .GroupJoin(
                context.Platforms.AsNoTracking(),
                x => x.Collection.PlatformId,
                platform => platform.Id,
                (x, platforms) => new { x.Collection, x.ItemCount, Platforms = platforms })
            .SelectMany(
                x => x.Platforms.DefaultIfEmpty(),
                (x, platform) => new { x.Collection, x.ItemCount, Platform = platform });

        if (platformId.HasValue)
        {
            query = query.Where(x => x.Collection.PlatformId == platformId.Value || x.Collection.PlatformId == null);
        }

        if (collectionId.HasValue)
        {
            query = query.Where(x => x.Collection.Id == collectionId.Value);
        }

        return query
            .OrderBy(x => context.LibraryCollections.Where(a => a.LibraryId == libraryId && a.CollectionId == x.Collection.Id).Select(a => a.SortOrder).First())
            .ThenBy(x => x.Collection.Name)
            .Select(x => new ConsumerCollectionReadModel(
                    x.Collection.Id,
                    x.Collection.Name,
                    x.Collection.Description,
                    x.Collection.CoverMediaId,
                    x.Collection.PlatformId,
                    x.Platform == null ? null : new SystemSummaryData(x.Platform.CanonicalKey ?? x.Platform.ShortName, x.Platform.Name, x.Platform.Name, null),
                    x.ItemCount,
                    context.LibraryCollections.Any(a => a.LibraryId == libraryId && a.CollectionId == x.Collection.Id && a.IsFeatured))
            { SortOrder = context.LibraryCollections.Where(a => a.LibraryId == libraryId && a.CollectionId == x.Collection.Id).Select(a => a.SortOrder).First() });
    }

    private ConsumerCollectionTitleReadModel AddReleaseSummary(
        ConsumerCollectionTitleReadModel item,
        IReadOnlyDictionary<int, IReadOnlyList<ConsumerReleaseSelectionCandidate>> releaseCandidatesByTitleId,
        ConsumerReleasePreference releasePreference)
    {
        var releaseCandidates = releaseCandidatesByTitleId.GetValueOrDefault(item.TitleId, []);
        var releases = releaseCandidates
            .Select(candidate => candidate.Release)
            .ToList();
        var defaultRelease = releaseSelector.SelectDefault(releaseCandidates, releasePreference);

        return item with
        {
            ReleaseCount = releases.Count,
            DefaultReleaseId = defaultRelease?.Id
        };
    }

    private IQueryable<MaterializedLibraryTitleEntity> OwnedMaterializedLibraryTitles(int libraryId) =>
        context.MaterializedLibraryTitles
            .AsNoTracking()
            .Where(title =>
                title.LibraryId == libraryId &&
                title.IsOwned &&
                context.Libraries.Any(library =>
                    library.Id == title.LibraryId &&
                    library.ConfigurationState == ValidConfigurationState));
}
