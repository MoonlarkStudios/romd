namespace Romd.Admin.Application.Source.Rom;

/// <summary>
///     Statistics about the ROM collection including status breakdowns.
/// </summary>
public sealed record CollectionStats
{
    /// <summary>
    ///     Total number of ROM files in the collection.
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
    public required long TotalSizeBytes { get; init; }

    /// <summary>
    ///     Total size on disk (compressed size).
    /// </summary>
    public required long TotalSizeOnDiskBytes { get; init; }

    public double CompressionRatio => TotalSizeBytes > 0 ? (double)TotalSizeOnDiskBytes / TotalSizeBytes : 1.0;
    public long BytesSaved => TotalSizeBytes - TotalSizeOnDiskBytes;

    /// <summary>
    ///     Breakdown of collection statistics by platform.
    /// </summary>
    public required IReadOnlyList<PlatformBreakdown> PlatformBreakdown { get; init; }
}

/// <summary>
///     Statistics for a single platform.
/// </summary>
public sealed record PlatformBreakdown
{
    /// <summary>
    ///     Platform ID.
    /// </summary>
    public required int PlatformId { get; init; }

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
