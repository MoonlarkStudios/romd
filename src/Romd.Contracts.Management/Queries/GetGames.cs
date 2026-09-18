namespace Romd.Contracts.Management.Queries;

/// <summary>
///     Query parameters for fetching paginated games.
/// </summary>
public sealed record GetGames
{
    /// <summary>
    ///     Pagination cursor, or null for the first page.
    /// </summary>
    public string? Cursor { get; init; }

    /// <summary>
    ///     Maximum number of items to return (1-100, default 50).
    /// </summary>
    public int Limit { get; init; } = 50;
}
