namespace Romd.Contracts.Management.Models;

/// <summary>
///     Aggregated filter facets with counts for catalog filtering.
/// </summary>
public sealed class CatalogFilters
{
    public required IReadOnlyList<FacetItem> Genres { get; init; }
    public required IReadOnlyList<FacetItem> Years { get; init; }
    public required IReadOnlyList<FacetItem> Manufacturers { get; init; }
    public required IReadOnlyList<FacetItem> Regions { get; init; }
    public required IReadOnlyList<FacetItem> Languages { get; init; }
    public required IReadOnlyList<FacetItem> ContentRatings { get; init; }
    public required IReadOnlyList<FacetItem> Platforms { get; init; }
    public required IReadOnlyList<FacetItem> EnrichmentStatuses { get; init; }
}

/// <summary>
///     A single facet value with its count.
/// </summary>
public sealed class FacetItem
{
    public required string Value { get; init; }
    public required int Count { get; init; }
}
