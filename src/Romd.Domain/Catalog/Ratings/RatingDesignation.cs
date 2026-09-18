namespace Romd.Domain.Catalog.Ratings;

/// <summary>
///     How a rating board classified the content. Values are persisted — do not renumber.
/// </summary>
public enum RatingDesignation
{
    /// <summary>A normal category with a minimum age.</summary>
    Rated = 0,

    /// <summary>Classification not yet assigned (ESRB RP, GRAC TESTING) — treated as unknown by policy.</summary>
    RatingPending = 1,

    /// <summary>The board refused to classify the content (ACB RC) — treated as maximally restricted by policy.</summary>
    RefusedClassification = 2
}
