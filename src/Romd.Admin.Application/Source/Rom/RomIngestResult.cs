using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom;

/// <summary>
///     Result of ROM file ingestion.
/// </summary>
public sealed record RomIngestResult
{
    /// <summary>
    ///     The ingested ROM file.
    /// </summary>
    public required RomFile RomFile { get; init; }

    /// <summary>
    ///     True if this ROM was newly created, false if it already existed (deduplicated).
    ///     Consumers can use this to return 201 Created vs 200 OK.
    /// </summary>
    public required bool IsNew { get; init; }

    /// <summary>
    ///     Titles this ROM matched (a ROM can match several). Captured for per-file provenance —
    ///     populated on both the new-ingest and deduplication paths.
    /// </summary>
    public IReadOnlyList<int> MatchedTitleIds { get; init; } = [];

    /// <summary>
    ///     The platform of the matched titles, if any.
    /// </summary>
    public int? PlatformId { get; init; }
}
