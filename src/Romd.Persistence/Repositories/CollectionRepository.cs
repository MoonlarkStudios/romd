using Romd.Application.Common.Systems;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Collections;
using Romd.Consumer.Application.Browse;
using Romd.Consumer.Application.Browse.ReadModels;
using Romd.Consumer.Application.Collections;
using Romd.Consumer.Application.Libraries;
using Romd.Domain.Collections;
using Romd.Domain.Libraries;
using Romd.Persistence.Entities;
using Romd.Persistence.Queries;

namespace Romd.Persistence.Repositories;

public sealed class CollectionRepository(
    RomdDbContext context,
    IConsumerReleaseSelector releaseSelector) : ICollectionRepository, IConsumerCollectionReadRepository
{
    private static readonly string ValidConfigurationState = LibraryConfigurationState.Valid.ToString();
    private readonly LiveConsumerLibraryQuery _liveConsumerLibrary = new(context);

    public async Task<Collection?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        var entity = await context.Collections
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        return entity?.ToDomain();
    }

    public async Task<Collection?> GetWithItemsAsync(int id, CancellationToken ct = default)
    {
        var entity = await context.Collections
            .AsTracking()
            .Include(c => c.Items.OrderBy(i => i.SortOrder))
            .FirstOrDefaultAsync(c => c.Id == id, ct);

        return entity?.ToDomainWithItems();
    }

    public async Task<IReadOnlyList<CollectionSummaryReadModel>> GetAllAsync(
        int? platformId = null,
        CancellationToken ct = default)
    {
        var query = context.Collections.AsNoTracking();

        if (platformId.HasValue)
            query = query.Where(c => c.PlatformId == platformId.Value || c.PlatformId == null);

        return await query
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Name)
            .Select(c => new CollectionSummaryReadModel(
                c.Id,
                c.Name,
                c.Description,
                c.CoverMediaId,
                c.PlatformId,
                c.IsSystem,
                c.SortOrder,
                c.CreatedAt,
                c.Items.Count))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<CollectionItemReadModel>> GetItemDetailsAsync(
        int collectionId,
        CancellationToken ct = default)
    {
        return await context.CollectionItems
            .AsNoTracking()
            .Where(i => i.CollectionId == collectionId)
            .Join(
                context.Titles,
                i => i.TitleId,
                t => t.Id,
                (i, t) => new { Item = i, Title = t })
            .OrderBy(x => x.Item.SortOrder)
            .Select(x => new CollectionItemReadModel(
                x.Item.TitleId,
                x.Title.Name,
                x.Title.PlatformId,
                x.Title.Media
                    .Where(m => m.Type == "Cover" && m.IsPrimary)
                    .Select(m => (int?)m.Id)
                    .FirstOrDefault(),
                x.Item.Note,
                x.Item.SortOrder,
                x.Item.AddedAt))
            .ToListAsync(ct);
    }

    public Task<ConsumerLibraryReadResult<IReadOnlyList<ConsumerCollectionReadModel>>> GetCollectionsAsync(
        ConsumerLibraryScope scope,
        int? platformId = null,
        CancellationToken ct = default) =>
        _liveConsumerLibrary.ReadAsync<IReadOnlyList<ConsumerCollectionReadModel>>(
            scope,
            async (libraryId, token) =>
                new ConsumerLibraryProjectionResult<IReadOnlyList<ConsumerCollectionReadModel>>.Found(
                    await GetCollectionsForLibraryAsync(libraryId, platformId, token)),
            ct);

    private async Task<IReadOnlyList<ConsumerCollectionReadModel>> GetCollectionsForLibraryAsync(
        int libraryId,
        int? platformId,
        CancellationToken ct)
    {
        var rows = await GetConsumerCollectionSummaries(libraryId, platformId).ToListAsync(ct);
        var systems = await new SystemSummaryReader(context).ReadAsync(rows.Where(x => x.PlatformId.HasValue).Select(x => x.PlatformId!.Value), ct);
        return rows.Select(x => x with { System = x.PlatformId is { } id ? systems[id] : null }).ToList();
    }

    public Task<ConsumerLibraryReadResult<ConsumerCollectionReadModel>> GetCollectionByIdAsync(
        ConsumerLibraryScope scope,
        int collectionId,
        CancellationToken ct = default) =>
        _liveConsumerLibrary.ReadAsync<ConsumerCollectionReadModel>(
            scope,
            (libraryId, token) => GetCollectionByIdForLibraryAsync(libraryId, collectionId, token),
            ct);

    private async Task<ConsumerLibraryProjectionResult<ConsumerCollectionReadModel>> GetCollectionByIdForLibraryAsync(
        int libraryId,
        int collectionId,
        CancellationToken ct)
    {
        var collection = await GetConsumerCollectionSummaries(libraryId, collectionId: collectionId)
            .FirstOrDefaultAsync(ct);
        if (collection?.PlatformId is { } id)
        {
            var systems = await new SystemSummaryReader(context).ReadAsync([id], ct);
            collection = collection with { System = systems[id] };
        }
        return collection is null
            ? new ConsumerLibraryProjectionResult<ConsumerCollectionReadModel>.ItemNotFound()
            : new ConsumerLibraryProjectionResult<ConsumerCollectionReadModel>.Found(collection);
    }

    public Task<ConsumerLibraryReadResult<IReadOnlyList<ConsumerCollectionTitleReadModel>>> GetCollectionTitlesAsync(
        ConsumerLibraryScope scope,
        int collectionId,
        ConsumerCollectionTitleCursor? cursor,
        ConsumerReleasePreference releasePreference,
        int limit,
        CancellationToken ct = default) =>
        _liveConsumerLibrary.ReadAsync<IReadOnlyList<ConsumerCollectionTitleReadModel>>(
            scope,
            (libraryId, token) => GetCollectionTitlesForLibraryAsync(
                libraryId,
                collectionId,
                cursor,
                releasePreference,
                limit,
                token),
            ct);

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

    public async Task<Collection> AddAsync(Collection collection, CancellationToken ct = default)
    {
        var entity = CollectionEntity.FromDomain(collection);
        context.Collections.Add(entity);
        await context.SaveChangesAsync(ct);

        return entity.ToDomainWithItems();
    }

    public async Task<CollectionSummaryReadModel?> UpdateDetailsStagedAsync(
        Collection collection,
        CancellationToken ct = default)
    {
        var row = await context.Collections
            .AsTracking()
            .Where(c => c.Id == collection.Id)
            .Select(c => new { Entity = c, ItemCount = c.Items.Count })
            .SingleOrDefaultAsync(ct);
        if (row is null) return null;

        row.Entity.Name = collection.Name;
        row.Entity.Description = collection.Description;
        row.Entity.CoverMediaId = collection.CoverMediaId;
        row.Entity.PlatformId = collection.PlatformId;

        return new CollectionSummaryReadModel(
            row.Entity.Id, row.Entity.Name, row.Entity.Description,
            row.Entity.CoverMediaId, row.Entity.PlatformId, row.Entity.IsSystem,
            row.Entity.SortOrder, row.Entity.CreatedAt, row.ItemCount);
    }

    public async Task UpdateAsync(Collection collection, CancellationToken ct = default)
    {
        var entity = await context.Collections
            .AsTracking()
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.Id == collection.Id, ct);

        if (entity is null) return;

        // Update collection properties
        entity.Name = collection.Name;
        entity.Description = collection.Description;
        entity.CoverMediaId = collection.CoverMediaId;
        entity.PlatformId = collection.PlatformId;
        entity.IsSystem = collection.IsSystem;
        entity.SortOrder = collection.SortOrder;

        // Sync items: remove deleted, update existing, add new
        var domainItems = collection.Items.ToList();
        var existingItemIds = entity.Items.Select(i => i.TitleId).ToHashSet();
        var domainItemIds = domainItems.Select(i => i.TitleId).ToHashSet();

        // Remove items no longer in domain
        entity.Items.RemoveAll(i => !domainItemIds.Contains(i.TitleId));

        // Update existing items
        foreach (var entityItem in entity.Items)
        {
            var domainItem = domainItems.First(d => d.TitleId == entityItem.TitleId);
            entityItem.SortOrder = domainItem.SortOrder;
            entityItem.Note = domainItem.Note;
        }

        // Add new items
        foreach (var domainItem in domainItems.Where(d => !existingItemIds.Contains(d.TitleId)))
        {
            entity.Items.Add(CollectionItemEntity.FromDomain(domainItem));
        }

        await context.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await context.Collections
            .Where(c => c.Id == id)
            .ExecuteDeleteAsync(ct);
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
                    context.LibraryCollections.Any(a => a.LibraryId == libraryId && a.CollectionId == x.Collection.Id && a.IsFeatured)));
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
