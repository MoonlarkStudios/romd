namespace Romd.Contracts.Management.Models;

/// <summary>
///     Content rating boards recognized by the admin contract. Serialized as named strings
///     (docs/decisions/api-contract-policy.md, "Opaque Public Identity"). Mirrors
///     <c>Romd.Domain.Catalog.Ratings.RatingBoard</c> member-for-member; contracts cannot
///     reference the domain, so keep the two in sync.
/// </summary>
public enum RatingBoard
{
    /// <summary>Entertainment Software Rating Board (North America).</summary>
    Esrb = 0,

    /// <summary>Pan-European Game Information.</summary>
    Pegi = 1,

    /// <summary>Computer Entertainment Rating Organization (Japan).</summary>
    Cero = 2,

    /// <summary>Unterhaltungssoftware Selbstkontrolle (Germany).</summary>
    Usk = 3,

    /// <summary>Game Rating and Administration Committee (South Korea).</summary>
    Grac = 4,

    /// <summary>Classificação Indicativa (Brazil).</summary>
    ClassInd = 5,

    /// <summary>Australian Classification Board.</summary>
    Acb = 6
}

/// <summary>
///     How a rating board classified the content. Serialized as a named string. Mirrors
///     <c>Romd.Domain.Catalog.Ratings.RatingDesignation</c> member-for-member; contracts cannot
///     reference the domain, so keep the two in sync.
/// </summary>
public enum RatingDesignation
{
    /// <summary>A normal category with a minimum age.</summary>
    Rated = 0,

    /// <summary>Classification not yet assigned (ESRB RP, GRAC TESTING).</summary>
    RatingPending = 1,

    /// <summary>The board refused to classify the content (ACB RC).</summary>
    RefusedClassification = 2
}
