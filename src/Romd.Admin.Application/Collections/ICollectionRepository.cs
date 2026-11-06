using ErrorOr;
using Romd.Application.Common.Pagination;
using Romd.Domain.Collections;

namespace Romd.Admin.Application.Collections;

/// <summary>
///     Read model for collection list queries, avoiding loading all items.
/// </summary>
public sealed record CollectionSummaryReadModel(
    int Id,
    string Name,
    string? Description,
    int? CoverMediaId,
    int? PlatformId,
    bool IsSystem,
    int SortOrder,
    DateTimeOffset CreatedAt,
    int ItemCount);

public interface ICollectionRepository
{
    Task<ErrorOr<PagedList<CollectionSummaryReadModel>>> GetPageAsync(int? platformId, string? query, string? cursor, int limit, CancellationToken ct);
    Task<ErrorOr<PagedList<CollectionItemReadModel>>> GetItemPageAsync(int collectionId, string? cursor, int limit, CancellationToken ct);
    Task<CollectionItemReadModel?> GetItemDetailAsync(int collectionId, int titleId, CancellationToken ct);
    Task<int> CountItemsAsync(int collectionId, CancellationToken ct);

    Task<Collection?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<Collection?> GetWithItemsAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<CollectionSummaryReadModel>> GetAllAsync(int? platformId = null, CancellationToken ct = default);
    Task<IReadOnlyList<CollectionItemReadModel>> GetItemDetailsAsync(int collectionId, CancellationToken ct = default);
    /// <summary>Stages a new, identity-free collection in the caller's transaction. The caller owns flush and commit.</summary>
    Task<IStagedCollectionCreation> AddStagedAsync(Collection collection, CancellationToken ct = default);
    /// <summary>Stages display details only, preserving membership. The caller owns the transaction.</summary>
    Task<CollectionSummaryReadModel?> UpdateDetailsStagedAsync(Collection collection, CancellationToken ct = default);

    /// <summary>
    ///     Locks the collection row for the caller's active transaction, then reads its current
    ///     details and membership. Item mutations call this before deciding anything from membership,
    ///     so concurrent item mutations on one collection serialize and each sees committed state.
    ///     Returns null when the collection does not exist. Requires an active transaction.
    /// </summary>
    Task<Collection?> LockForItemMutationStagedAsync(int collectionId, CancellationToken ct = default);

    /// <summary>Stages insertion of one new item. Never touches other items or collection details.</summary>
    Task AddItemStagedAsync(CollectionItem item, CancellationToken ct = default);

    /// <summary>Stages deletion of one item. Never touches other items or collection details.</summary>
    Task RemoveItemStagedAsync(int collectionId, int titleId, CancellationToken ct = default);

    /// <summary>Stages the note of one existing item. Leaves its order, AddedAt, and other items unchanged.</summary>
    Task UpdateItemNoteStagedAsync(CollectionItem item, CancellationToken ct = default);

    /// <summary>
    ///     Stages the sort order of each supplied existing item. Never inserts or deletes membership,
    ///     and leaves notes, AddedAt, and collection details unchanged.
    /// </summary>
    Task ApplyItemOrderStagedAsync(int collectionId, IReadOnlyCollection<CollectionItem> items, CancellationToken ct = default);

    Task DeleteAsync(int id, CancellationToken ct = default);
}

/// <summary>
///     Reads the created aggregate and its generated identity after the caller flushes staging.
///     The receipt belongs to the current unit-of-work scope; reading it does not commit creation.
/// </summary>
public interface IStagedCollectionCreation
{
    Collection ReadAfterFlush();
}

/// <summary>
///     Read model for collection items with joined title data.
/// </summary>
public sealed record CollectionItemReadModel(
    int TitleId,
    string TitleName,
    int PlatformId,
    int? CoverMediaId,
    string? Note,
    int SortOrder,
    DateTimeOffset AddedAt);

public sealed record CollectionItemPosition(int SortOrder, int TitleId);
