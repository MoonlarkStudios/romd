using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Contracts.Common.Artwork;

namespace Romd.Contracts.Consumer.Collections;

public sealed record ConsumerCollection
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public SystemSummary? System { get; init; }
    public string? CoverUrl { get; init; }
    public string? HeroUrl { get; init; }
    public int ItemCount { get; init; }
    public bool IsFeatured { get; init; } = true;
}

public sealed record ConsumerCollectionTitle
{
    public IReadOnlyList<ResolvedArtwork> Artwork { get; init; } = [];
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required SystemSummary System { get; init; }
    public string? CoverUrl { get; init; }
    public string? Genre { get; init; }
    public DateOnly? ReleaseDate { get; init; }
    public double? Rating { get; init; }
    public required int ReleaseCount { get; init; }
    public string? DefaultReleaseId { get; init; }
}
