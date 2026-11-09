using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Contracts.Common.Artwork;
using Romd.Contracts.Consumer.Releases;
using Romd.Contracts.Common.Models;

namespace Romd.Contracts.Consumer.Browse;

public sealed record ConsumerTitleCard
{
    public IReadOnlyList<ResolvedArtwork> Artwork { get; init; } = [];
    public required string Id { get; init; }
    public required SystemSummary System { get; init; }
    public IReadOnlyList<ConsumerContentRating> ContentRatings { get; init; } = [];
    public required string Name { get; init; }
    public string? CoverUrl { get; init; }
    public string? Genre { get; init; }
    public DateOnly? ReleaseDate { get; init; }
    public int? Players { get; init; }
    public string? EsrbRating { get; init; }
    public double? Rating { get; init; }
    public required int ReleaseCount { get; init; }
    public string? DefaultReleaseId { get; init; }
}

public sealed record ConsumerTitleDetail
{
    public IReadOnlyList<ResolvedArtwork> Artwork { get; init; } = [];
    public required string Id { get; init; }
    public required SystemSummary System { get; init; }
    public IReadOnlyList<ConsumerContentRating> ContentRatings { get; init; } = [];
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? Publisher { get; init; }
    public string? Developer { get; init; }
    public string? Genre { get; init; }
    public DateOnly? ReleaseDate { get; init; }
    public int? Players { get; init; }
    public double? Rating { get; init; }
    public required Page<ConsumerMediaRef> Media { get; init; }
    public required Page<ConsumerRelease> Releases { get; init; }
    public string? DefaultReleaseId { get; init; }
}

/// <summary>Effective classification using the canonical board name and code. Unknown boards remain extensible.</summary>
public sealed record ConsumerContentRating
{
    public required string Board { get; init; }
    public required string Code { get; init; }
    public required string BoardName { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public ReferenceAsset? Icon { get; init; }
}
