using Romd.Contracts.Common.ReferenceCatalog;
using Romd.Contracts.Common.Artwork;
using Romd.Contracts.Consumer.Releases;

namespace Romd.Contracts.Consumer.Browse;

public sealed record ConsumerTitleCardDto
{
    public IReadOnlyList<ResolvedArtworkDto> Artwork { get; init; } = [];
    public required string Id { get; init; }
    public required SystemSummaryDto System { get; init; }
    public IReadOnlyList<ConsumerContentRatingDto> ContentRatings { get; init; } = [];
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

public sealed record ConsumerTitleDetailDto
{
    public IReadOnlyList<ResolvedArtworkDto> Artwork { get; init; } = [];
    public required string Id { get; init; }
    public required SystemSummaryDto System { get; init; }
    public IReadOnlyList<ConsumerContentRatingDto> ContentRatings { get; init; } = [];
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? Publisher { get; init; }
    public string? Developer { get; init; }
    public string? Genre { get; init; }
    public DateOnly? ReleaseDate { get; init; }
    public int? Players { get; init; }
    public double? Rating { get; init; }
    public required IReadOnlyList<ConsumerMediaRefDto> Media { get; init; }
    public required IReadOnlyList<ConsumerReleaseDto> Releases { get; init; }
    public string? DefaultReleaseId { get; init; }
}

/// <summary>Effective classification using the canonical board name and code. Unknown boards remain extensible.</summary>
public sealed record ConsumerContentRatingDto
{
    public required string Board { get; init; }
    public required string Code { get; init; }
    public required string BoardName { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public ReferenceAssetDto? Icon { get; init; }
}
