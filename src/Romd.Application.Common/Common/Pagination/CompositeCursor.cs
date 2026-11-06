using System.Text.Json.Serialization;

namespace Romd.Application.Common.Pagination;

/// <summary>
///     Sort fields available for game search results.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GameSortField
{
    /// <summary>
    ///     Sort by game name (ascending).
    /// </summary>
    Name,

    /// <summary>
    ///     Sort by release year (descending, nulls last).
    /// </summary>
    Year,

    /// <summary>
    ///     Sort by full-text relevance rank (descending, id ascending within equal ranks).
    ///     Only applicable when a search query is provided.
    /// </summary>
    Relevance
}

/// <summary>
///     Composite keyset cursor for game search with dynamic sort orders. Name/Year seek on the sort
///     column; Relevance seeks on the invariant-formatted rank. The cursor is opaque to clients -
///     they don't need to know the underlying strategy.
/// </summary>
/// <param name="SortField">The field being sorted by.</param>
/// <param name="SortValue">The value of the sort column (or rank) for keyset pagination.</param>
/// <param name="Id">The unique ID tie-breaker for keyset pagination.</param>
public sealed record CompositeCursor(
    [property: JsonPropertyName("f")] GameSortField SortField,
    [property: JsonPropertyName("v")] string? SortValue,
    [property: JsonPropertyName("i")] int Id);

/// <summary>
///     Composite keyset cursor for title search. Name/Rating seek on the sort column; Relevance
///     seeks on the invariant-formatted rank.
/// </summary>
/// <param name="SortField">The title sort field.</param>
/// <param name="SortValue">The value of the sort column (or rank) for keyset pagination.</param>
/// <param name="Id">The unique ID tie-breaker for keyset pagination.</param>
public sealed record TitleCompositeCursor(
    [property: JsonPropertyName("f")] int SortField,
    [property: JsonPropertyName("v")] string? SortValue,
    [property: JsonPropertyName("i")] int Id);
