namespace Romd.Application.Common.Pagination;

/// <summary>
///     A paginated result set using keyset/cursor pagination.
/// </summary>
/// <typeparam name="T">The type of items in the list.</typeparam>
/// <param name="Items">The items for the current page.</param>
/// <param name="NextCursor">The cursor to fetch the next page, or null if this is the last page.</param>
/// <param name="HasNextPage">Whether there are more items after this page.</param>
public record PagedList<T>(
    IReadOnlyList<T> Items,
    string? NextCursor,
    bool HasNextPage);
