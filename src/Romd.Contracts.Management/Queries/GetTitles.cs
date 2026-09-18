namespace Romd.Contracts.Management.Queries;

/// <summary>
///     Query parameters for fetching paginated titles.
/// </summary>
public sealed record GetTitles
{
    /// <summary>
    ///     Filter by owned status (true = owned only, false = not owned, null = all).
    /// </summary>
    public bool? Owned { get; init; }

    /// <summary>
    ///     Pagination cursor, or null for the first page.
    /// </summary>
    public string? Cursor { get; init; }

    /// <summary>
    ///     Maximum number of items to return (1-100, default 50).
    /// </summary>
    public int Limit { get; init; } = 50;
}
