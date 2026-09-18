using Romd.Contracts.Common.Artwork;

namespace Romd.Contracts.Management.Models;

/// <summary>
///     Lightweight title representation for catalog grid display.
/// </summary>
public sealed record CatalogTitle
{
    public IReadOnlyList<ResolvedArtworkDto> Artwork { get; init; } = [];

    /// <summary>
    ///     Sqid-encoded title ID.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    ///     Sqid-encoded platform ID.
    /// </summary>
    public required string SystemKey { get; init; }

    /// <summary>
    ///     Title display name.
    /// </summary>
    public required string Name { get; init; }
    public string EnrichmentStatus { get; init; } = "None";

    /// <summary>
    ///     Whether the user owns any ROM files for this title.
    /// </summary>
    public bool HasLocalPayload { get; init; }

    /// <summary>
    ///     Whether this title is part of the curated collection target (counts toward coverage
    ///     and the default enrichment scope).
    /// </summary>
    public bool IsTracked { get; init; }

    /// <summary>
    ///     Number of game versions (releases) the user owns ROMs for.
    /// </summary>
    public int LocalPayloadVersionCount { get; init; }

    /// <summary>
    ///     Total number of game versions (releases) available for this title.
    /// </summary>
    public int TotalVersionCount { get; init; }

    /// <summary>
    ///     URL to the cover art image, or null if no cover is available.
    /// </summary>
    public string? CoverUrl { get; init; }

    /// <summary>
    ///     Genre of the title.
    /// </summary>
    public string? Genre { get; init; }

    /// <summary>
    ///     Release date of the title.
    /// </summary>
    public DateOnly? ReleaseDate { get; init; }

    /// <summary>
    ///     User/critic rating (0-100 scale).
    /// </summary>
    public double? Rating { get; init; }
}
