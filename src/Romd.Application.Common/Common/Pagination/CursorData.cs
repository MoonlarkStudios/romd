using System.Text.Json.Serialization;

namespace Romd.Application.Common.Pagination;

/// <summary>
///     Generic cursor data for keyset pagination.
///     Contains the sort key value and unique ID for resuming from a specific position.
/// </summary>
/// <typeparam name="T">The type of the sort value (e.g., string for Name, DateTime for dates).</typeparam>
/// <param name="SortValue">The value of the column being sorted by (e.g., "Super Mario", "1994").</param>
/// <param name="Id">The unique ID tie-breaker for stable ordering.</param>
public record CursorData<T>(
    [property: JsonPropertyName("s")] T SortValue,
    [property: JsonPropertyName("i")] int Id);
