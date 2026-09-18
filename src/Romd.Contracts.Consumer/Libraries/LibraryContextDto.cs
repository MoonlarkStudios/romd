using Romd.Contracts.Consumer.Collections;

namespace Romd.Contracts.Consumer.Libraries;

public sealed record LibraryContextDto
{
    public required string Name { get; init; }
    public required LibraryContentCountsDto Counts { get; init; }
    public required IReadOnlyList<LibraryFacetDto> Platforms { get; init; }
    public required IReadOnlyList<LibraryFacetDto> Genres { get; init; }
    public required IReadOnlyList<ConsumerCollectionDto> FeaturedCollections { get; init; }
}

public sealed record LibraryContentCountsDto
{
    public int OwnedTitleCount { get; init; }
    public int AvailableTitleCount { get; init; }
    public int CollectionCount { get; init; }
}

public sealed record LibraryFacetDto(
    string Id,
    string Name,
    int Count);
