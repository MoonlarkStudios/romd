namespace Romd.Domain.Jobs;

/// <summary>
///     Outcome of attempting to ingest a single ROM file.
/// </summary>
public enum RomIngestOutcome
{
    /// <summary>
    ///     ROM was successfully ingested and stored.
    /// </summary>
    Ingested = 0,

    /// <summary>
    ///     ROM already exists (deduplicated by hash).
    /// </summary>
    Deduplicated = 1,

    /// <summary>
    ///     ROM was rejected (unidentified, no matching DAT entry).
    /// </summary>
    Rejected = 2,

    /// <summary>
    ///     ROM processing failed due to an error.
    /// </summary>
    Failed = 3
}
