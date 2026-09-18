using Romd.Application.Common.Systems;
using Romd.Domain.Catalog;

namespace Romd.Consumer.Application.Browse.ReadModels;

public sealed record ConsumerReleaseData
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public string? Revision { get; init; }
    public required IReadOnlyList<string> Regions { get; init; }
    public required IReadOnlyList<string> Languages { get; init; }
    public required long SizeBytes { get; init; }
    public required bool IsComplete { get; init; }
}

public sealed record ConsumerPlatformSummaryData
{
    public required string Key { get; init; }
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required string ShortName { get; init; }
    public string? Manufacturer { get; init; }
    public required int TitleCount { get; init; }
    public int? CoverMediaId { get; init; }
}

public sealed record ConsumerPlatformDetailData
{
    public required string Key { get; init; }
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required string ShortName { get; init; }
    public string? Manufacturer { get; init; }
    public required int TitleCount { get; init; }
    public required IReadOnlyList<ConsumerMediaData> Media { get; init; }
}

public sealed record ConsumerTitleCardData
{
    public IReadOnlyList<ArtworkResolution> Artwork { get; init; } = [];
    public required int Id { get; init; }
    public required int PlatformId { get; init; }
    public required SystemSummaryData System { get; init; }
    public IReadOnlyList<ConsumerContentRatingData> ContentRatings { get; init; } = [];
    public required string Name { get; init; }
    public int? CoverMediaId { get; init; }
    public string? Genre { get; init; }
    public DateOnly? ReleaseDate { get; init; }
    public int? Players { get; init; }
    public string? EsrbRating { get; init; }
    public double? Rating { get; init; }
    public required int ReleaseCount { get; init; }
    public int? DefaultReleaseId { get; init; }
}

public sealed record ConsumerTitleDetailData
{
    public IReadOnlyList<ArtworkResolution> Artwork { get; init; } = [];
    public required int Id { get; init; }
    public required int PlatformId { get; init; }
    public required SystemSummaryData System { get; init; }
    public IReadOnlyList<ConsumerContentRatingData> ContentRatings { get; init; } = [];
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? Publisher { get; init; }
    public string? Developer { get; init; }
    public string? Genre { get; init; }
    public DateOnly? ReleaseDate { get; init; }
    public int? Players { get; init; }
    public double? Rating { get; init; }
    public required IReadOnlyList<ConsumerMediaData> Media { get; init; }
    public required IReadOnlyList<ConsumerReleaseData> Releases { get; init; }
    public int? DefaultReleaseId { get; init; }
}

public sealed record ConsumerMediaData
{
    public required int Id { get; init; }
    public required string Type { get; init; }
    public required bool IsPrimary { get; init; }
}

public sealed record ConsumerContentRatingData
{
    public required Romd.Domain.Catalog.Ratings.RatingBoard Board { get; init; }
    public required string Code { get; init; }
    public string? BoardName { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
    public Romd.Application.Common.ReferenceCatalog.ReferenceAssetData? Icon { get; init; }
}
