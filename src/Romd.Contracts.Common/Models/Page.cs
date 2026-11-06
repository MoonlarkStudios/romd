namespace Romd.Contracts.Common.Models;

/// <summary>
///     A page of results from a paginated query.
/// </summary>
public sealed class Page<T>
{
    public required IReadOnlyList<T> Items { get; init; }
    public string? NextCursor { get; init; }
    public required bool HasNextPage { get; init; }
}
