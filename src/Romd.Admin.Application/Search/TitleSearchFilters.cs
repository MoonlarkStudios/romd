using System.Text.Json.Serialization;

namespace Romd.Admin.Application.Search;

/// <summary>
///     Filter parameters for title search queries.
/// </summary>
/// <param name="PlatformId">Filter by platform ID (resolved from Sqid).</param>
/// <param name="Genre">Filter by genre.</param>
/// <param name="ReleaseCompleteness">Filter by DAT release completeness (All, Complete, Partial, None).</param>
/// <param name="EnrichmentStatus">Filter by enrichment status (None, Pending, Completed, Failed, NotFound).</param>
public sealed record TitleSearchFilters(
    int? PlatformId = null,
    string? Genre = null,
    ReleaseCompletenessFilter ReleaseCompleteness = ReleaseCompletenessFilter.All,
    string? EnrichmentStatus = null,
    TrackedFilter Tracked = TrackedFilter.All);

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
