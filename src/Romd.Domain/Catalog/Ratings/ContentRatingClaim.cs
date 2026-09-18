namespace Romd.Domain.Catalog.Ratings;

/// <summary>
///     A raw content rating assertion from a metadata layer (provider or user).
///     Claims are stored verbatim: providers never compute ages or normalized levels.
///     Canonicalization happens in <see cref="RatingBoardCatalog" /> at rematerialization,
///     so a provider data quirk never corrupts stored claims.
/// </summary>
public sealed record ContentRatingClaim
{
    public required RatingBoard Board { get; init; }

    /// <summary>The source's literal label, e.g. "E10", "PEGI Twelve".</summary>
    public required string RawCode { get; init; }

    /// <summary>Provider-side row identifier, for idempotent refresh.</summary>
    public string? ExternalRatingId { get; init; }

    /// <summary>Content descriptors, e.g. "Fantasy Violence".</summary>
    public IReadOnlyList<string> Descriptors { get; init; } = [];

    public string? Synopsis { get; init; }
}
