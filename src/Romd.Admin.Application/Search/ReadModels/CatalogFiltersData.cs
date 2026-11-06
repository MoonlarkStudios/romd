namespace Romd.Admin.Application.Search.ReadModels;

/// <summary>
///     Aggregated filter facets with counts for catalog filtering.
/// </summary>
public sealed class CatalogFiltersData
{
    public required IReadOnlyList<FacetItemData> Genres { get; init; }
    public required IReadOnlyList<FacetItemData> Years { get; init; }
    public required IReadOnlyList<FacetItemData> Manufacturers { get; init; }
    public required IReadOnlyList<FacetItemData> Regions { get; init; }
    public required IReadOnlyList<FacetItemData> Languages { get; init; }
    public required IReadOnlyList<FacetItemData> ContentRatings { get; init; }
    public required IReadOnlyList<FacetItemData> Platforms { get; init; }
    public required IReadOnlyList<FacetItemData> EnrichmentStatuses { get; init; }
}

/// <summary>
///     A single facet value with its count.
/// </summary>
public sealed class FacetItemData
{
    public required string Value { get; init; }
    public required int Count { get; init; }
}
