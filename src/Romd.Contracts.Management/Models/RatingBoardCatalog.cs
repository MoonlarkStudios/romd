namespace Romd.Contracts.Management.Models;

/// <summary>
///     Content rating boards recognized by the admin contract. Serialized as named strings
///     (docs/decisions/admin-api-contract-policy.md, "Opaque Public Identity"). Mirrors
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

/// <summary>
///     The canonical content rating board catalog — the closed set of boards and the categories
///     each can assign. Reference data for admin surfaces (e.g. the re-rate picker) so valid codes
///     come from the domain's single source of truth rather than a duplicated client list.
/// </summary>
public sealed record RatingBoardCatalogResponse
{
    public required IReadOnlyList<RatingBoardInfo> Boards { get; init; }
}

public sealed record RatingBoardInfo
{
    /// <summary>The rating board, serialized as its named string (e.g. "Esrb", "Pegi").</summary>
    public required RatingBoard Board { get; init; }

    /// <summary>The board's domain name (e.g. "Esrb", "Pegi", "ClassInd").</summary>
    public required string Name { get; init; }

    /// <summary>The categories this board can assign, ordered youngest-first.</summary>
    public required IReadOnlyList<RatingCategoryInfo> Categories { get; init; }
}

public sealed record RatingCategoryInfo
{
    /// <summary>Canonical display code (e.g. "E10+", "PEGI 12", "CERO B").</summary>
    public required string Code { get; init; }

    /// <summary>How the board classified the content, serialized as its named string.</summary>
    public required RatingDesignation Designation { get; init; }

    /// <summary>Minimum age for rated categories; null for pending/refused classifications.</summary>
    public int? MinimumAge { get; init; }
}
