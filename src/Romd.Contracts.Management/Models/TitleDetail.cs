using Romd.Contracts.Common.Artwork;
using Romd.Contracts.Common.Models;

namespace Romd.Contracts.Management.Models;

/// <summary>
///     Comprehensive title detail aggregating catalog, registry, and inventory data.
///     Represents the "Trinity" view: Title -> Releases (DatGames) -> Files (DatRoms).
/// </summary>
public sealed record TitleDetail
{
    public IReadOnlyList<ResolvedArtworkDto> Artwork { get; init; } = [];
    // === Catalog Layer (Title) ===
    public required string Id { get; init; }
    public required string SystemKey { get; init; }
    public required string Name { get; init; }
    public required string EnrichmentStatus { get; init; }

    // Enriched metadata
    public string? Description { get; init; }
    public string? Publisher { get; init; }
    public string? Developer { get; init; }
    public string? Genre { get; init; }
    public DateOnly? ReleaseDate { get; init; }
    public int? Players { get; init; }
    public double? Rating { get; init; }
    public IReadOnlyList<TitleContentRating> ContentRatings { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastEnrichedAt { get; init; }

    /// <summary>
    ///     Tracks which source provided each field value.
    ///     Keys are property names, values are source IDs (e.g., "user", "igdb").
    ///     Only present when metadata layer system is active.
    /// </summary>
    public Dictionary<string, string>? FieldProvenance { get; init; }

    // Media URLs (cover art, screenshots)
    public IReadOnlyList<TitleMediaRef> Media { get; init; } = [];

    // === Inventory Summary ===

    /// <summary>
    ///     True when any effective source assertion for this title has locally stored payload.
    /// </summary>
    public bool HasLocalPayload { get; init; }

    /// <summary>
    ///     Whether this title is part of the curated collection target (counts toward coverage and
    ///     the default enrichment scope). Set automatically when first owned; toggled manually.
    /// </summary>
    public bool IsTracked { get; init; }

    /// <summary>
    ///     The pinned canonical catalog release, or null when global release preferences apply.
    /// </summary>
    public string? PinnedCatalogReleaseId { get; init; }

    /// <summary>
    ///     Completion percentage: owned files / total required files across all releases.
    /// </summary>
    public double CompletionPercent { get; init; }

    // === Registry Layer (Releases) ===

    /// <summary>
    ///     All DatGame entries (releases/versions) linked to this title.
    ///     Each release contains its file requirements with ownership status.
    /// </summary>
    public IReadOnlyList<TitleRelease> Releases { get; init; } = [];
}

/// <summary>
///     A release/version of a title from a DAT file.
///     Examples: "Super Mario World (USA)", "Super Mario World (Europe)", "Super Mario World (USA) (Rev 1)".
/// </summary>
public sealed record TitleRelease
{
    /// <summary>
    ///     Stable canonical catalog release ID. Null only for an unprojected source game.
    /// </summary>
    public string? CatalogReleaseId { get; init; }
    public required string Id { get; init; }
    public required string DatId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? Year { get; init; }
    public string? Manufacturer { get; init; }

    // Parsed metadata from DAT entry
    public string? Region { get; init; }
    public string? Language { get; init; }
    public string? Revision { get; init; }

    /// <summary>
    ///     Whether this specific release is fully owned (all required files present).
    /// </summary>
    public bool IsComplete { get; init; }

    /// <summary>
    ///     File requirements for this release.
    /// </summary>
    public IReadOnlyList<TitleFileRequirement> Files { get; init; } = [];

    /// <summary>
    ///     The individual DAT entries that declare this canonical release (its provenance).
    ///     One release may be declared by multiple DATs.
    /// </summary>
    public IReadOnlyList<TitleReleaseSource> Sources { get; init; } = [];
}

/// <summary>
///     A single DAT entry contributing to a release — links a canonical release back to the
///     individual DatGame in a specific DAT file.
/// </summary>
public sealed record TitleReleaseSource
{
    public required string DatGameId { get; init; }
    public required string DatId { get; init; }
    public required string DatName { get; init; }
    public required string GameName { get; init; }
}

public sealed record TitleContentRating
{
    public required RatingBoard Board { get; init; }
    public required string Code { get; init; }
    public required RatingDesignation Designation { get; init; }
    public int? MinimumAge { get; init; }
    public required string SourceId { get; init; }
    public string? ExternalRatingId { get; init; }
    public IReadOnlyList<string> Descriptors { get; init; } = [];
    public string? Synopsis { get; init; }
}

/// <summary>
///     A file requirement from a DatRom with ownership status.
/// </summary>
public sealed record TitleFileRequirement
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public ByteCount Size { get; init; }

    // Hash values for verification
    public string? Sha1 { get; init; }
    public string? Md5 { get; init; }
    public string? Crc { get; init; }

    /// <summary>
    ///     Status from DAT (good, baddump, nodump).
    /// </summary>
    public string? Status { get; init; }

    /// <summary>
    ///     Whether this file is owned (RomFileId is linked).
    /// </summary>
    public bool IsOwned { get; init; }

    /// <summary>
    ///     The linked RomFile ID, if owned.
    /// </summary>
    public string? RomFileId { get; init; }
}

/// <summary>
///     Reference to a media asset for a title.
/// </summary>
public sealed record TitleMediaRef
{
    /// <summary>
    ///     Unique ID of the media asset.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    ///     Type of media (Cover, Screenshot, Banner, Logo, etc.).
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    ///     URL to access the media file.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    ///     Source identifier: "user", "igdb", "screenscraper", etc.
    /// </summary>
    public required string SourceId { get; init; }
    public string? Attribution { get; init; }
    public string? SourcePageUrl { get; init; }

    /// <summary>
    ///     Whether this is the primary (displayed) media for this type.
    /// </summary>
    public required bool IsPrimary { get; init; }

    /// <summary>
    ///     The stored media blob in CAS (its sizes and compression). Null when the file is missing.
    /// </summary>
    public StoredFileRef? File { get; init; }
}
