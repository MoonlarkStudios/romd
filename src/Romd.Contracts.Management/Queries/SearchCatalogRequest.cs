namespace Romd.Contracts.Management.Queries;

/// <summary>
///     Request parameters for searching the catalog (Titles).
/// </summary>
public sealed record SearchCatalogRequest
{
    /// <summary>
    ///     Full-text search query. When null, returns all titles matching filters.
    /// </summary>
    public string? Query { get; init; }

    /// <summary>
    ///     Filter by platform ID (Sqid-encoded).
    /// </summary>
    public string? SystemKey { get; init; }

    /// <summary>
    ///     Filter by genre.
    /// </summary>
    public string? Genre { get; init; }

    /// <summary>
    ///     Only return titles with at least one owned ROM file. Defaults to true.
    /// </summary>
    public bool OwnedOnly { get; init; } = true;

    /// <summary>
    ///     Sort field: "name", "rating", or "relevance" (default: "name").
    /// </summary>
    public string? SortBy { get; init; }

    /// <summary>
    ///     Cursor for pagination.
    /// </summary>
    public string? Cursor { get; init; }

    /// <summary>
    ///     Maximum number of results to return (default: 50, max: 100).
    /// </summary>
    public int Limit { get; init; } = 50;
}
