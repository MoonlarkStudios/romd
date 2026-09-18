namespace Romd.Contracts.Management.Queries;

/// <summary>
///     Query parameters for fetching paginated ROM files from the library.
/// </summary>
public sealed record GetRoms
{
    /// <summary>
    ///     Filter by catalog status: "unidentified", "unrouted", "cataloged", or null for all.
    /// </summary>
    public string? Status { get; init; }

    /// <summary>
    ///     Pagination cursor, or null for the first page.
    /// </summary>
    public string? Cursor { get; init; }

    /// <summary>
    ///     Maximum number of items to return (1-100, default 50).
    /// </summary>
    public int Limit { get; init; } = 50;
}
