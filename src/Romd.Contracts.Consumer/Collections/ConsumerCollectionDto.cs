using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Contracts.Common.Artwork;

namespace Romd.Contracts.Consumer.Collections;

public sealed record ConsumerCollectionDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public SystemSummaryDto? System { get; init; }
    public string? CoverUrl { get; init; }
    public string? HeroUrl { get; init; }
    public int ItemCount { get; init; }
    public bool IsFeatured { get; init; } = true;
}

public sealed record ConsumerCollectionTitleDto
{
    public IReadOnlyList<ResolvedArtworkDto> Artwork { get; init; } = [];
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required SystemSummaryDto System { get; init; }
    public string? CoverUrl { get; init; }
    public string? Genre { get; init; }
    public DateOnly? ReleaseDate { get; init; }
    public double? Rating { get; init; }
    public required int ReleaseCount { get; init; }
    public string? DefaultReleaseId { get; init; }
}
