using Romd.Contracts.Common.Models;

namespace Romd.Contracts.Management.Models;

/// <summary>
///     Summary of library health across identification, routing, coverage, and storage metrics.
/// </summary>
public sealed record LibrarySummary
{
    public required int TotalRoms { get; init; }

    public required int IdentifiedCount { get; init; }
    public required int UnidentifiedCount { get; init; }

    public required int RoutedCount { get; init; }
    public required int UnroutedCount { get; init; }

    public required int ExpectedTitleCount { get; init; }
    public required int LocalPayloadTitleCount { get; init; }
    public required decimal CoverageHealthPercent { get; init; }

    public required ByteCount TotalStorageBytes { get; init; }
    public required ByteCount TotalStorageBytesOnDisk { get; init; }
    public required int CompressedFileCount { get; init; }
    public required int UncompressedFileCount { get; init; }
    public required double AverageCompressionRatio { get; init; }
    public required ByteCount BytesSaved { get; init; }

    public DateTimeOffset? LastDatRefreshAt { get; init; }
}
