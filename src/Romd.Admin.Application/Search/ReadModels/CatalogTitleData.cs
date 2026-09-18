using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Search.ReadModels;

/// <summary>
///     Internal read model for catalog title search results.
///     Lightweight for grid display.
/// </summary>
public sealed record CatalogTitleData
{
    public IReadOnlyList<ArtworkResolution> Artwork { get; init; } = [];

    public required int Id { get; init; }
    public required int PlatformId { get; init; }
    public required string Name { get; init; }
    public string EnrichmentStatus { get; init; } = "None";
    public bool HasLocalPayload { get; init; }
    public bool IsTracked { get; init; }
    public int LocalPayloadVersionCount { get; init; }
    public int TotalVersionCount { get; init; }
    public int? CoverMediaId { get; init; }
    public string? Genre { get; init; }
    public DateOnly? ReleaseDate { get; init; }
    public double? Rating { get; init; }
}
