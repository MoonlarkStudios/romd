using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Titles.ReadModels;

/// <summary>
///     Internal read model for title detail queries.
///     Optimized for the TitleDetail endpoint that shows Title -> Releases -> Files.
/// </summary>
public sealed record TitleDetailData
{
    public IReadOnlyList<ArtworkResolution> Artwork { get; init; } = [];
    public required int Id { get; init; }
    public required int PlatformId { get; init; }
    public required string Name { get; init; }
    public required string EnrichmentStatus { get; init; }
    public required bool IsTracked { get; init; }
    public bool HasLocalPayload { get; init; }
    public int? PinnedCatalogReleaseId { get; init; }

    public string? Description { get; init; }
    public string? Publisher { get; init; }
    public string? Developer { get; init; }
    public string? Genre { get; init; }
    public DateOnly? ReleaseDate { get; init; }
    public int? Players { get; init; }
    public double? Rating { get; init; }
    public IReadOnlyList<TitleContentRatingData> ContentRatings { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastEnrichedAt { get; init; }

    /// <summary>
    ///     Tracks which source provided each field value.
    ///     Keys are property names, values are source IDs (e.g., "user", "igdb").
    /// </summary>
    public IReadOnlyDictionary<string, string>? FieldProvenance { get; init; }

    public IReadOnlyList<TitleMediaData> Media { get; init; } = [];
    public IReadOnlyList<TitleReleaseData> Releases { get; init; } = [];
}

/// <summary>
///     Media asset data for a title.
/// </summary>
public sealed record TitleMediaData
{
    public required int Id { get; init; }
    public required string Type { get; init; }
    public required int FileId { get; init; }
    public required string SourceId { get; init; }
    public string? Attribution { get; init; }
    public string? SourcePageUrl { get; init; }
    public required bool IsPrimary { get; init; }

    /// <summary>Logical size in bytes of the stored media file (from CAS); null if missing.</summary>
    public long? SizeBytes { get; init; }

    /// <summary>On-disk (possibly compressed) size in bytes of the stored media file (from CAS); null if missing.</summary>
    public long? SizeOnDiskBytes { get; init; }

    /// <summary>Whether the stored media blob is compressed on disk; null if missing.</summary>
    public bool? IsCompressed { get; init; }
}

public sealed record TitleContentRatingData
{
    public required int Board { get; init; }
    public required string Code { get; init; }
    public required int Designation { get; init; }
    public int? MinimumAge { get; init; }
    public required string SourceId { get; init; }
    public string? ExternalRatingId { get; init; }
    public IReadOnlyList<string> Descriptors { get; init; } = [];
    public string? Synopsis { get; init; }
}

/// <summary>
///     A canonical release of a title. <see cref="Id"/> is the representative DatGame so admin
///     actions keep a concrete target; <see cref="Sources"/> links the contributing DAT entries.
/// </summary>
public sealed record TitleReleaseData
{
    public int? CatalogReleaseId { get; init; }
    public required int Id { get; init; }
    public required int DatFileId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? Year { get; init; }
    public string? Manufacturer { get; init; }
    public string? Region { get; init; }
    public string? Language { get; init; }
    public string? Revision { get; init; }
    public IReadOnlyList<TitleFileData> Files { get; init; } = [];
    public IReadOnlyList<TitleReleaseSourceData> Sources { get; init; } = [];
}

/// <summary>
///     One source DAT entry contributing to a release (its provenance).
/// </summary>
public sealed record TitleReleaseSourceData
{
    public required int DatGameId { get; init; }
    public required int DatFileId { get; init; }
    public required string DatName { get; init; }
    public required string GameName { get; init; }
}

/// <summary>
///     File requirement data from a DatRom with ownership status.
/// </summary>
public sealed record TitleFileData
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public long Size { get; init; }
    public string? Sha1 { get; init; }
    public string? Md5 { get; init; }
    public string? Crc { get; init; }
    public string? Status { get; init; }
    public int? RomFileId { get; init; }
}
