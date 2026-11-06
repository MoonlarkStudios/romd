using System.Text.Json.Serialization;

namespace Romd.Admin.Application.Search;

/// <summary>
///     Filter parameters for title search queries.
/// </summary>
/// <param name="PlatformId">Filter by platform ID (resolved from Sqid).</param>
/// <param name="Genre">Filter by genre.</param>
/// <param name="ReleaseCompleteness">Filter by DAT release completeness (All, Complete, Partial, None).</param>
/// <param name="EnrichmentStatus">Filter by last enrichment attempt (None, Pending, Completed, Failed, NotFound, LowConfidence).</param>
/// <param name="Enrichment">Assessment flags a title must all carry; <see cref="EnrichmentFlagFilter.Settled"/> alone means none.</param>
public sealed record TitleSearchFilters(
    int? PlatformId = null,
    string? Genre = null,
    ReleaseCompletenessFilter ReleaseCompleteness = ReleaseCompletenessFilter.All,
    string? EnrichmentStatus = null,
    TrackedFilter Tracked = TrackedFilter.All,
    string? ArtworkStatus = null,
    IReadOnlyCollection<EnrichmentFlagFilter>? Enrichment = null,
    Romd.Admin.Application.TrackedCollection.TrackedCollectionView? ReleaseReview = null);

/// <summary>
///     Current enrichment assessment flags for catalog search, in chip order. Flags combine with AND.
/// </summary>
public enum EnrichmentFlagFilter
{
    /// <summary>At least one requirement needs a curator decision.</summary>
    NeedsReview,

    /// <summary>At least one requirement is blocked by provider configuration or availability.</summary>
    Blocked,

    /// <summary>Enrichment or artwork work is active for the title.</summary>
    Processing,

    /// <summary>At least one requirement can be enriched automatically.</summary>
    Automatic,

    /// <summary>None of the four flags apply. Mutually exclusive with the other values.</summary>
    Settled
}

/// <summary>
///     Tracked-status filter options for catalog search.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TrackedFilter
{
    /// <summary>Show all titles regardless of tracked status.</summary>
    All,

    /// <summary>Show only tracked titles (the curated collection target).</summary>
    Tracked,

    /// <summary>Show only untracked titles.</summary>
    Untracked
}

/// <summary>
///     DAT release-completeness filter options for catalog search.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReleaseCompletenessFilter
{
    /// <summary>
    ///     Show all titles regardless of DAT release completeness.
    /// </summary>
    All,

    /// <summary>
    ///     Show only titles where every DAT release has local payload.
    /// </summary>
    Complete,

    /// <summary>
    ///     Show only titles where some but not all DAT releases have local payload.
    /// </summary>
    Partial,

    /// <summary>
    ///     Show only titles with DAT releases but no local payload for any release.
    /// </summary>
    None
}

/// <summary>
///     Sort fields available for title search results.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TitleSortField
{
    /// <summary>
    ///     Sort by name ascending.
    /// </summary>
    Name,

    /// <summary>
    ///     Sort by rating descending (highest first).
    /// </summary>
    Rating,

    /// <summary>
    ///     Sort by full-text relevance rank (only valid when query is provided).
    /// </summary>
    Relevance
}
