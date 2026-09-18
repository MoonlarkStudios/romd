using Romd.Contracts.Common.Models;

namespace Romd.Contracts.Management.Models;

/// <summary>
///     Unified system dashboard statistics.
/// </summary>
public sealed record SystemStats
{
    /// <summary>
    ///     Total number of ROM files in the library.
    /// </summary>
    public required int TotalRomFiles { get; init; }

    /// <summary>
    ///     Number of ROM files fully cataloged with a Title.
    /// </summary>
    public required int CatalogedCount { get; init; }

    /// <summary>
    ///     Number of ROM files not matched by any DAT.
    /// </summary>
    public required int UnidentifiedCount { get; init; }

    /// <summary>
    ///     Number of ROM files matched by DAT but without a Title (DAT has no platform).
    /// </summary>
    public required int UnroutedCount { get; init; }

    /// <summary>
    ///     Total size of all ROM files in bytes.
    /// </summary>
    public required ByteCount TotalSizeBytes { get; init; }

    /// <summary>
    ///     Total number of titles in the system.
    /// </summary>
    public required int TotalTitles { get; init; }

    /// <summary>
    ///     Number of titles that have at least one local ROM file.
    /// </summary>
    public required int TitlesWithLocalPayload { get; init; }

    /// <summary>
    ///     Enrichment task statistics.
    /// </summary>
    public required EnrichmentStats Enrichment { get; init; }

    /// <summary>
    ///     Total number of platforms in the system.
    /// </summary>
    public required int TotalPlatforms { get; init; }

    /// <summary>
    ///     Platform breakdown with title counts.
    /// </summary>
    public required IReadOnlyList<PlatformSummary> Platforms { get; init; }
}

/// <summary>
///     Statistics about enrichment status across titles.
/// </summary>
public sealed record EnrichmentStats
{
    /// <summary>
    ///     Number of titles pending enrichment.
    /// </summary>
    public required int Pending { get; init; }

    /// <summary>
    ///     Number of titles successfully enriched.
    /// </summary>
    public required int Completed { get; init; }

    /// <summary>
    ///     Number of titles where no match was found in external sources.
    /// </summary>
    public required int NotFound { get; init; }

    /// <summary>
    ///     Number of titles where enrichment failed.
    /// </summary>
    public required int Failed { get; init; }
}

/// <summary>
///     Summary of a platform for dashboard display.
/// </summary>
public sealed record PlatformSummary
{
    /// <summary>
    ///     Stable system key.
    /// </summary>
    public required string SystemKey { get; init; }

    /// <summary>
    ///     Platform display name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    ///     Total number of titles known for this platform.
    /// </summary>
    public required int TotalTitles { get; init; }

    /// <summary>
    ///     Number of titles that have local ROM files (owned).
    /// </summary>
    public required int LocalPayloadTitles { get; init; }
}
