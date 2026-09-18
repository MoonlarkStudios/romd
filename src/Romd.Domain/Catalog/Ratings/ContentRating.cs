namespace Romd.Domain.Catalog.Ratings;

/// <summary>
///     An effective, canonicalized rating for one board, materialized onto a Title.
///     One per board; produced by <see cref="RatingBoardCatalog.TryResolve(ContentRatingClaim, string)" />
///     during rematerialization.
/// </summary>
public sealed record ContentRating
{
    public required RatingBoard Board { get; init; }

    /// <summary>Canonical display code: "E10+", "PEGI 12", "CERO B".</summary>
    public required string Code { get; init; }

    public required RatingDesignation Designation { get; init; }

    /// <summary>
    ///     Youngest age the board deems the content suitable for, conservative reading.
    ///     Null iff <see cref="Designation" /> is not <see cref="RatingDesignation.Rated" />.
    /// </summary>
    public int? MinimumAge { get; init; }

    public IReadOnlyList<string> Descriptors { get; init; } = [];

    public string? Synopsis { get; init; }

    /// <summary>Layer that supplied the winning claim: "igdb", "user", or a future provider.</summary>
    public required string SourceId { get; init; }

    /// <summary>Provider-side row identifier, for idempotent refresh.</summary>
    public string? ExternalRatingId { get; init; }
}
