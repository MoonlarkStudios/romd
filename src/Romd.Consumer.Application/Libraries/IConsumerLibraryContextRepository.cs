namespace Romd.Consumer.Application.Libraries;

public sealed record ConsumerLibraryContextReadModel(
    string Name,
    ConsumerLibraryContentCountsReadModel Counts,
    IReadOnlyList<ConsumerLibraryPlatformFacetReadModel> Platforms,
    IReadOnlyList<ConsumerLibraryGenreFacetReadModel> Genres,
    IReadOnlyList<ConsumerLibraryCollectionFacetReadModel> FeaturedCollections);

public sealed record ConsumerLibraryContentCountsReadModel(
    int OwnedTitleCount,
    int AvailableTitleCount,
    int CollectionCount);

public sealed record ConsumerLibraryPlatformFacetReadModel(
    string SystemKey,
    string PlatformName,
    int Count);

public sealed record ConsumerLibraryGenreFacetReadModel(
    string Genre,
    int Count);

public sealed record ConsumerLibraryCollectionFacetReadModel(
    int CollectionId,
    string CollectionName,
    int MatchingCount);

public interface IConsumerLibraryContextRepository
{
    Task<ConsumerLibraryReadResult<ConsumerLibraryContextReadModel>> GetCurrentContextAsync(
        ConsumerLibraryScope scope,
        CancellationToken ct = default);
}
