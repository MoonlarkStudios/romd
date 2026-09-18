using Romd.Contracts.Common.Models;

namespace Romd.Contracts.Management.Models;

/// <summary>
///     A title a ROM matched during import.
/// </summary>
public sealed record JobItemTitleDto(string Id, string Name);

/// <summary>
///     Per-file provenance for an upload job: what happened to one ROM or DAT and where it landed.
/// </summary>
public sealed record JobItemDto
{
    public required string Id { get; init; }

    /// <summary>"rom" or "dat".</summary>
    public required string Kind { get; init; }

    public required string FileName { get; init; }
    public required ByteCount SizeBytes { get; init; }

    /// <summary>"ingested", "deduplicated", "rejected", "failed", "dat_routed", or "dat_unrouted".</summary>
    public required string Outcome { get; init; }

    /// <summary>The stored ROM (ingest) or the existing ROM it deduplicated to.</summary>
    public string? RomFileId { get; init; }

    public string? DatFileId { get; init; }
    public string? SystemKey { get; init; }
    public string? PlatformName { get; init; }

    public required IReadOnlyList<JobItemTitleDto> MatchedTitles { get; init; }

    /// <summary>Number of games in an imported DAT catalog.</summary>
    public int? GameCount { get; init; }

    /// <summary>
    ///     The upload mode requested for this ROM: true for archive-only mode, false for collection
    ///     mode, and null for DAT or historical provenance.
    /// </summary>
    public bool? ArchiveOnly { get; init; }

    public string? Error { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
///     A keyset page of provenance records. Pass NextCursor to retrieve the next page
///     with the same outcome and search filters.
/// </summary>
public sealed record JobItemPage
{
    public required IReadOnlyList<JobItemDto> Items { get; init; }
    public bool HasMore { get; init; }
    public string? NextCursor { get; init; }
}
