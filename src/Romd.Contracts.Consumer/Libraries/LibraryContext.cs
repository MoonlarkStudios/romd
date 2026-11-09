using Romd.Contracts.Consumer.Collections;

namespace Romd.Contracts.Consumer.Libraries;

public sealed record LibraryContext
{
    public required string Name { get; init; }
    public required LibraryContentCounts Counts { get; init; }
    public required IReadOnlyList<LibraryFacet> Platforms { get; init; }
    public required IReadOnlyList<LibraryFacet> Genres { get; init; }
    public required IReadOnlyList<ConsumerCollection> FeaturedCollections { get; init; }
}

public sealed record LibraryContentCounts
{
    public int OwnedTitleCount { get; init; }
    public int AvailableTitleCount { get; init; }
    public int CollectionCount { get; init; }
}

public sealed record LibraryFacet(
    string Id,
    string Name,
    int Count);
