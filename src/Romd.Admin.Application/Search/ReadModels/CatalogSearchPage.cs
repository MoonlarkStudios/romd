using Romd.Application.Common.Pagination;

namespace Romd.Admin.Application.Search.ReadModels;

/// <summary>Catalog page with an exact total on the first page; cursor pages omit it.</summary>
public sealed record CatalogSearchPage(
    IReadOnlyList<CatalogTitleData> Items,
    string? NextCursor,
    bool HasNextPage,
    int? TotalCount) : PagedList<CatalogTitleData>(Items, NextCursor, HasNextPage);
