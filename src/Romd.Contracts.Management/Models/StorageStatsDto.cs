using Romd.Contracts.Common.Models;

namespace Romd.Contracts.Management.Models;

public sealed record StorageStatsDto
{
    public required ByteCount TotalStorageBytes { get; init; }
    public required ByteCount TotalStorageBytesOnDisk { get; init; }
    public required int CompressedFileCount { get; init; }
    public required int UncompressedFileCount { get; init; }
    public required double AverageCompressionRatio { get; init; }
    public required ByteCount BytesSaved { get; init; }

    /// <summary>
    ///     Per-owner attribution of stored bytes (ROM matched/unmatched, DAT, media, unattributed).
    ///     Categories may overlap under content-addressed dedup, so this is attribution rather than a
    ///     strict partition of <see cref="TotalStorageBytesOnDisk" />.
    /// </summary>
    public required IReadOnlyList<StorageCategoryDto> Breakdown { get; init; }
}

public sealed record StorageCategoryDto
{
    /// <summary>Stable category key: rom_matched, rom_unmatched, dat, media, unattributed.</summary>
    public required string Category { get; init; }
    public required int FileCount { get; init; }
    public required ByteCount SizeBytes { get; init; }
    public required ByteCount SizeOnDiskBytes { get; init; }
}
