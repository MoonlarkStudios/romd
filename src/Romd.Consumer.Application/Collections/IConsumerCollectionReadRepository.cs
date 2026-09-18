using Romd.Application.Common.Systems;
using Romd.Consumer.Application.Browse;
using Romd.Consumer.Application.Browse.ReadModels;
using Romd.Consumer.Application.Libraries;
using Romd.Domain.Catalog;

namespace Romd.Consumer.Application.Collections;

public sealed record ConsumerCollectionReadModel(
    int Id,
    string Name,
    string? Description,
    int? CoverMediaId,
    int? PlatformId,
    SystemSummaryData? System,
    int ItemCount,
    bool IsFeatured = true);

public sealed record ConsumerCollectionTitleReadModel(
    int TitleId,
    string TitleName,
    int PlatformId,
    SystemSummaryData System,
    int? CoverMediaId,
    string? Genre,
    DateOnly? ReleaseDate,
    double? Rating,
    int ReleaseCount,
    int? DefaultReleaseId,
    int SortOrder)
{
    public IReadOnlyList<ArtworkResolution> Artwork { get; init; } = [];
}

public sealed record ConsumerCollectionTitleCursor(int SortOrder, int TitleId);

public interface IConsumerCollectionReadRepository
{
    Task<ConsumerLibraryReadResult<IReadOnlyList<ConsumerCollectionReadModel>>> GetCollectionsAsync(
        ConsumerLibraryScope scope,
        int? platformId = null,
        CancellationToken ct = default);

    Task<ConsumerLibraryReadResult<ConsumerCollectionReadModel>> GetCollectionByIdAsync(
        ConsumerLibraryScope scope,
        int collectionId,
        CancellationToken ct = default);

    Task<ConsumerLibraryReadResult<IReadOnlyList<ConsumerCollectionTitleReadModel>>> GetCollectionTitlesAsync(
        ConsumerLibraryScope scope,
        int collectionId,
        ConsumerCollectionTitleCursor? cursor,
        ConsumerReleasePreference releasePreference,
        int limit,
        CancellationToken ct = default);
}
