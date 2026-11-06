using Romd.Domain.Catalog.Ratings;

namespace Romd.Admin.Application.Titles.Enrichment;

/// <summary>
///     Value object containing enrichment data from a metadata provider.
///     Used to merge data from multiple providers.
/// </summary>
public sealed record EnrichmentData
{
    public string? Description { get; init; }
    public string? Publisher { get; init; }
    public string? Developer { get; init; }
    public string? Genre { get; init; }
    public DateOnly? ReleaseDate { get; init; }
    public int? Players { get; init; }
    public double? Rating { get; init; }
    public IReadOnlyList<ContentRatingClaim>? ContentRatings { get; init; }

    /// <summary>
    ///     Merges this data with another, filling in null fields.
    ///     Existing (non-null) fields are preserved.
    /// </summary>
    public EnrichmentData MergeWith(EnrichmentData other)
    {
        return this with
        {
            Description = Description ?? other.Description,
            Publisher = Publisher ?? other.Publisher,
            Developer = Developer ?? other.Developer,
            Genre = Genre ?? other.Genre,
            ReleaseDate = ReleaseDate ?? other.ReleaseDate,
            Players = Players ?? other.Players,
            Rating = Rating ?? other.Rating,
            ContentRatings = ContentRatings is { Count: > 0 } ? ContentRatings : other.ContentRatings
        };
    }

    /// <summary>
    ///     Checks if all essential metadata fields are populated.
    /// </summary>
    public bool IsComplete()
    {
        return Description != null
               && Publisher != null
               && Developer != null
               && Genre != null;
    }

    /// <summary>
    ///     Checks if any data is present.
    /// </summary>
    public bool HasAnyData()
    {
        return Description != null
               || Publisher != null
               || Developer != null
               || Genre != null
               || ReleaseDate != null
               || Players != null
               || Rating != null
               || ContentRatings is { Count: > 0 };
    }
}
