namespace Romd.Consumer.Application.Browse;

public sealed record ConsumerCatalogFilters(
    string? Query,
    int? PlatformId,
    string? Genre,
    ConsumerCompletenessFilter Completeness,
    ConsumerTitleSortField SortField);

public enum ConsumerCompletenessFilter
{
    All,
    Complete,
    Partial
}

public enum ConsumerTitleSortField
{
    Name,
    Rating
}
