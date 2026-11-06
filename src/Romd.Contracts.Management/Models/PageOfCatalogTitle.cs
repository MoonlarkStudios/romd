namespace Romd.Contracts.Management.Models;

/// <summary>A catalog page. TotalCount is supplied on the first page only.</summary>
public sealed class PageOfCatalogTitle
{
    public required IReadOnlyList<CatalogTitle> Items { get; init; }
    public string? NextCursor { get; init; }
    public required bool HasNextPage { get; init; }
    public int? TotalCount { get; init; }
}
