using Romd.Domain.Catalog.Ratings;

namespace Romd.Domain.Catalog;

/// <summary>
///     Typed representation of metadata fields that can be stored in a layer.
///     Null values mean "this source has no data for this field" and are not serialized.
/// </summary>
public sealed record TitleMetadataPayload
{
    public string? ProviderGameId { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string? Publisher { get; init; }
    public string? Developer { get; init; }
    public string? Genre { get; init; }
    public DateOnly? ReleaseDate { get; init; }
    public int? Players { get; init; }
    public double? Rating { get; init; }

    /// <summary>
    ///     Raw per-board rating claims from this source. Canonicalized by
    ///     <see cref="RatingBoardCatalog" /> at rematerialization, never here.
    /// </summary>
    public IReadOnlyList<ContentRatingClaim>? ContentRatings { get; init; }

    /// <summary>
    ///     Checks if this payload has any non-null values.
    /// </summary>
    public bool HasAnyValue()
    {
        return Name != null
               || Description != null
               || Publisher != null
               || Developer != null
               || Genre != null
               || ReleaseDate.HasValue
               || Players.HasValue
               || Rating.HasValue
               || ContentRatings is { Count: > 0 };
    }
}
