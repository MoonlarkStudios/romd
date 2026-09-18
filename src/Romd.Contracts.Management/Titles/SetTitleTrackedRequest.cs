namespace Romd.Contracts.Management.Titles;

/// <summary>
///     Request to set whether a title is part of the curated collection target.
/// </summary>
public sealed record SetTitleTrackedRequest
{
    /// <summary>
    ///     True to track the title (include it in coverage and default enrichment), false to untrack it.
    /// </summary>
    public required bool Tracked { get; init; }

    /// <summary>
    ///     Optional Sqid-encoded canonical catalog release to pin. The release must belong to
    ///     the title. Null clears an existing pin when tracking is enabled.
    /// </summary>
    public string? PinnedCatalogReleaseId { get; init; }
}
