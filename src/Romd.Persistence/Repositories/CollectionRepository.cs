using ErrorOr;
using Romd.Application.Common.Pagination;
using Romd.Application.Common.Validation;
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

public sealed partial class CollectionRepository(
    RomdDbContext context,
    IConsumerReleaseSelector releaseSelector) : ICollectionRepository, IConsumerCollectionReadRepository
{
    private static readonly string ValidConfigurationState = LibraryConfigurationState.Valid.ToString();
    private readonly LiveConsumerLibraryQuery _liveConsumerLibrary = new(context);

    public async Task<ErrorOr<PagedList<CollectionSummaryReadModel>>> GetPageAsync(int? platformId, string? query, string? cursor, int limit, CancellationToken ct)
    {
        if (limit is < 1 or > 100)
            return FieldErrors.Validation("Collections.InvalidLimit", "Use a limit between 1 and 100.", "limit");
        string? term = query?.Trim().ToLowerInvariant();
        string fingerprint = KeysetCursor.Fingerprint("admin.collections", platformId, term, "sortOrder,name,id:asc");
        var decoded = KeysetCursor.Decode<CollectionPosition>(cursor, fingerprint, p => p.Id > 0 && p.Name is not null);
        if (decoded.IsError)
            return FieldErrors.Validation("Collections.InvalidCursor", "The cursor is invalid for this query.", "cursor");
        var rows = context.Collections.AsNoTracking();
        if (platformId.HasValue)
            rows = rows.Where(c => c.PlatformId == platformId || c.PlatformId == null);
        if (!string.IsNullOrEmpty(term))
            rows = rows.Where(c => c.Name.ToLower().Contains(term));
        if (decoded.Value is { } position)
            rows = rows.Where(c => c.SortOrder > position.Order || c.SortOrder == position.Order && (string.Compare(c.Name, position.Name) > 0 || c.Name == position.Name && c.Id > position.Id));
        var page = await rows.OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ThenBy(c => c.Id)
            .Select(c => new CollectionSummaryReadModel(c.Id, c.Name, c.Description, c.CoverMediaId, c.PlatformId, c.IsSystem, c.SortOrder, c.CreatedAt, c.Items.Count)).Take(limit + 1).ToListAsync(ct);
        return new PagedList<CollectionSummaryReadModel>(page.Take(limit).ToArray(), page.Count > limit ? KeysetCursor.Encode(new CollectionPosition(page[limit - 1].SortOrder, page[limit - 1].Name, page[limit - 1].Id), fingerprint) : null, page.Count > limit);
    }

    public async Task<ErrorOr<PagedList<CollectionItemReadModel>>> GetItemPageAsync(int collectionId, string? cursor, int limit, CancellationToken ct)
    {
        if (!await context.Collections.AnyAsync(c => c.Id == collectionId, ct))
            return CollectionErrors.NotFound();
        if (limit is < 1 or > 100)
            return FieldErrors.Validation("Collections.InvalidLimit", "Use a limit between 1 and 100.", "limit");
        string fingerprint = KeysetCursor.Fingerprint("admin.collection.items", collectionId, "sortOrder,titleId:asc");
        var decoded = KeysetCursor.Decode<CollectionItemPosition>(cursor, fingerprint, p => p.TitleId > 0);
        if (decoded.IsError)
            return FieldErrors.Validation("Collections.InvalidCursor", "The cursor is invalid for this collection.", "cursor");
        var query = context.CollectionItems.AsNoTracking().Where(i => i.CollectionId == collectionId);
        if (decoded.Value is { } position)
            query = query.Where(i => i.SortOrder > position.SortOrder || i.SortOrder == position.SortOrder && i.TitleId > position.TitleId);
        var page = await query
            .Join(context.Titles, item => item.TitleId, title => title.Id, (item, title) => new { Item = item, Title = title })
            .OrderBy(row => row.Item.SortOrder).ThenBy(row => row.Item.TitleId).Take(limit + 1)
            .Select(row => new CollectionItemReadModel(row.Item.TitleId, row.Title.Name, row.Title.PlatformId,
                row.Title.Media.Where(media => media.Type == "Cover" && media.IsPrimary).Select(media => (int?)media.Id).FirstOrDefault(),
                row.Item.Note, row.Item.SortOrder, row.Item.AddedAt))
            .ToListAsync(ct);
        return new PagedList<CollectionItemReadModel>(page.Take(limit).ToArray(), page.Count > limit ? KeysetCursor.Encode(new CollectionItemPosition(page[limit - 1].SortOrder, page[limit - 1].TitleId), fingerprint) : null, page.Count > limit);
    }

    public Task<CollectionItemReadModel?> GetItemDetailAsync(int collectionId, int titleId, CancellationToken ct) =>
        ItemDetails(context.CollectionItems.AsNoTracking().Where(i => i.CollectionId == collectionId && i.TitleId == titleId)).SingleOrDefaultAsync(ct);
    public Task<int> CountItemsAsync(int collectionId, CancellationToken ct) => context.CollectionItems.CountAsync(i => i.CollectionId == collectionId, ct);
    private IQueryable<CollectionItemReadModel> ItemDetails(IQueryable<CollectionItemEntity> query) =>
        query.Join(context.Titles, i => i.TitleId, t => t.Id, (i, t) => new CollectionItemReadModel(i.TitleId, t.Name, t.PlatformId, t.Media.Where(m => m.Type == "Cover" && m.IsPrimary).Select(m => (int?)m.Id).FirstOrDefault(), i.Note, i.SortOrder, i.AddedAt));
    private sealed record CollectionPosition(int Order, string Name, int Id);

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
            .AsNoTracking()
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

    public Task<IStagedCollectionCreation> AddStagedAsync(Collection collection, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (collection.Id != 0)
            throw new ArgumentException("Collection creation requires a new collection without an identity.", nameof(collection));
        if (context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Collection creation requires the caller's transaction.");

        var entity = CollectionEntity.FromDomain(collection);
        context.Collections.Add(entity);
        return Task.FromResult<IStagedCollectionCreation>(new StagedCollectionCreation(entity));
    }

    private sealed class StagedCollectionCreation(CollectionEntity entity) : IStagedCollectionCreation
    {
        public Collection ReadAfterFlush() => entity.Id > 0
            ? entity.ToDomainWithItems()
            : throw new InvalidOperationException("Flush staged collection creation before reading its identity.");
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
        if (row is null)
            return null;

        row.Entity.Name = collection.Name;
        row.Entity.Description = collection.Description;
        row.Entity.CoverMediaId = collection.CoverMediaId;
        row.Entity.PlatformId = collection.PlatformId;

        return new CollectionSummaryReadModel(
            row.Entity.Id, row.Entity.Name, row.Entity.Description,
            row.Entity.CoverMediaId, row.Entity.PlatformId, row.Entity.IsSystem,
            row.Entity.SortOrder, row.Entity.CreatedAt, row.ItemCount);
    }

    public async Task<Collection?> LockForItemMutationStagedAsync(
        int collectionId,
        CancellationToken ct = default)
    {
        if (context.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "Collection item mutations must lock the collection inside the caller's transaction.");
        }

        // Taken before membership is read: under READ COMMITTED each later statement sees every
        // item mutation that committed while this request waited for the lock.
        var locked = await context.Database
            .SqlQuery<int>($"""SELECT "Id" AS "Value" FROM romd."Collections" WHERE "Id" = {collectionId} FOR UPDATE""")
            .ToListAsync(ct);
        if (locked.Count == 0)
            return null;

        var entity = await context.Collections
            .AsNoTracking()
            .Include(c => c.Items.OrderBy(i => i.SortOrder))
            .SingleAsync(c => c.Id == collectionId, ct);
        return entity.ToDomainWithItems();
    }

    public async Task AddItemStagedAsync(CollectionItem item, CancellationToken ct = default) =>
        await context.CollectionItems.AddAsync(CollectionItemEntity.FromDomain(item), ct);

    public async Task RemoveItemStagedAsync(int collectionId, int titleId, CancellationToken ct = default)
    {
        var entity = await FindItemForUpdateAsync(collectionId, titleId, ct);
        if (entity is not null)
            context.CollectionItems.Remove(entity);
    }

    public async Task UpdateItemNoteStagedAsync(CollectionItem item, CancellationToken ct = default)
    {
        var entity = await FindItemForUpdateAsync(item.CollectionId, item.TitleId, ct);
        if (entity is not null)
            entity.Note = item.Note;
    }

    public async Task ApplyItemOrderStagedAsync(
        int collectionId,
        IReadOnlyCollection<CollectionItem> items,
        CancellationToken ct = default)
    {
        var sortOrders = items.ToDictionary(i => i.TitleId, i => i.SortOrder);
        var titleIds = sortOrders.Keys.ToList();
        var entities = await context.CollectionItems
            .AsTracking()
            .Where(i => i.CollectionId == collectionId && titleIds.Contains(i.TitleId))
            .ToListAsync(ct);
        foreach (var entity in entities)
        {
            entity.SortOrder = sortOrders[entity.TitleId];
        }
    }

    private Task<CollectionItemEntity?> FindItemForUpdateAsync(int collectionId, int titleId, CancellationToken ct) =>
        context.CollectionItems
            .AsTracking()
            .SingleOrDefaultAsync(i => i.CollectionId == collectionId && i.TitleId == titleId, ct);

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await context.Collections
            .Where(c => c.Id == id)
            .ExecuteDeleteAsync(ct);
    }


}
