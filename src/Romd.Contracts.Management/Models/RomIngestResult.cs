namespace Romd.Contracts.Management.Models;

/// <summary>
///     Result of a ROM file ingestion operation.
/// </summary>
public sealed record RomIngestResult
{
    public required Rom Rom { get; init; }

    /// <summary>
    ///     True if this ROM was newly created, false if it already existed (deduplicated).
    /// </summary>
    public required bool IsNew { get; init; }
}
