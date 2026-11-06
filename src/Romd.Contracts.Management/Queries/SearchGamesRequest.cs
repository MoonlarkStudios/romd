namespace Romd.Contracts.Management.Queries;

/// <summary>
///     Query parameters for searching games with full-text search.
/// </summary>
public sealed record SearchGamesRequest
{
    /// <summary>
    ///     Full-text search query. Optional; when omitted, returns filtered results without FTS.
    /// </summary>
    public string? Query { get; init; }

    /// <summary>
    ///     Filter by platform ID (Sqid format).
    /// </summary>
    public string? SystemKey { get; init; }

    /// <summary>
    ///     Filter by release year.
    /// </summary>
    public string? Year { get; init; }

    /// <summary>
    ///     Filter by manufacturer name.
    /// </summary>
    public string? Manufacturer { get; init; }

    /// <summary>
    ///     Filter by region.
    /// </summary>
    public string? Region { get; init; }

    /// <summary>
    ///     How to handle BIOS entries: "exclude" (default), "include", or "only".
    /// </summary>
    public string? Bios { get; init; }

    /// <summary>
    ///     Sort field: "name" (default), "year", or "relevance" (requires query).
    /// </summary>
    public string? SortBy { get; init; }

    /// <summary>
    ///     Pagination cursor, or null for the first page.
    /// </summary>
    public string? Cursor { get; init; }

    /// <summary>
    ///     Maximum number of items to return (1-100, default 50).
    /// </summary>
    public int Limit { get; init; } = 50;
}
