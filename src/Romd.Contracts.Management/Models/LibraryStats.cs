using Romd.Contracts.Common.Models;

namespace Romd.Contracts.Management.Models;

/// <summary>
///     Statistics about the user's ROM library.
/// </summary>
public sealed record LibraryStats
{
    /// <summary>
    ///     Total number of ROM files in the library.
    /// </summary>
    public required int TotalRomFiles { get; init; }

    /// <summary>
    ///     Number of ROM files not matched by any DAT.
    /// </summary>
    public required int UnidentifiedCount { get; init; }

    /// <summary>
    ///     Number of ROM files matched by DAT but without a Title (DAT has no platform).
    /// </summary>
    public required int UnroutedCount { get; init; }

    /// <summary>
    ///     Number of ROM files fully cataloged with a Title.
    /// </summary>
    public required int CatalogedCount { get; init; }

    /// <summary>
    ///     Total size of all ROM files in bytes.
    /// </summary>
    public required ByteCount TotalSizeBytes { get; init; }

    /// <summary>
    ///     Total size on disk (compressed).
    /// </summary>
    public required ByteCount TotalSizeOnDiskBytes { get; init; }

    public required double CompressionRatio { get; init; }
    public required ByteCount BytesSaved { get; init; }

    /// <summary>
    ///     Breakdown of library statistics by platform.
    /// </summary>
    public required IReadOnlyList<PlatformBreakdown> PlatformBreakdown { get; init; }
}

/// <summary>
///     Library statistics for a single platform.
/// </summary>
public sealed record PlatformBreakdown
{
    /// <summary>
    ///     Platform ID.
    /// </summary>
    public required string SystemKey { get; init; }

    /// <summary>
    ///     Platform display name.
    /// </summary>
    public required string PlatformName { get; init; }

    /// <summary>
    ///     Number of Titles that have local ROM files (owned).
    /// </summary>
    public required int LocalPayloadCount { get; init; }

    /// <summary>
    ///     Total number of Titles for this platform.
    /// </summary>
    public required int TotalCount { get; init; }
}
