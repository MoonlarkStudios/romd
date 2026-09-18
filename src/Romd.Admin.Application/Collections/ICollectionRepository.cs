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
    Task<Collection?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<Collection?> GetWithItemsAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<CollectionSummaryReadModel>> GetAllAsync(int? platformId = null, CancellationToken ct = default);
    Task<IReadOnlyList<CollectionItemReadModel>> GetItemDetailsAsync(int collectionId, CancellationToken ct = default);
    Task<Collection> AddAsync(Collection collection, CancellationToken ct = default);
    /// <summary>Stages display details only, preserving membership. The caller owns the transaction.</summary>
    Task<CollectionSummaryReadModel?> UpdateDetailsStagedAsync(Collection collection, CancellationToken ct = default);
    Task UpdateAsync(Collection collection, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
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
