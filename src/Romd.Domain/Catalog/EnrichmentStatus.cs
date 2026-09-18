namespace Romd.Domain.Catalog;

/// <summary>
///     Status of metadata enrichment from external sources (IGDB, etc.)
/// </summary>
public enum EnrichmentStatus
{
    /// <summary>
    ///     Not yet attempted to enrich.
    /// </summary>
    None = 0,

    /// <summary>
    ///     Queued for enrichment, waiting to be processed.
    /// </summary>
    Pending = 1,

    /// <summary>
    ///     Successfully enriched with external metadata.
    /// </summary>
    Completed = 2,

    /// <summary>
    ///     Enrichment failed due to an error (network, API, etc.)
    /// </summary>
    Failed = 3,

    /// <summary>
    ///     Enrichment completed but no match was found in the external source.
    ///     This is a valid terminal state for homebrew, hacks, etc.
    /// </summary>
    NotFound = 4,

    /// <summary>
    ///     A match was found but confidence is below the auto-enrich threshold.
    ///     Data is stored in layers but not materialized until confirmed by user.
    /// </summary>
    LowConfidence = 5
}
