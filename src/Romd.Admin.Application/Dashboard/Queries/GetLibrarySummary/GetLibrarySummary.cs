using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Storage.Files;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Management.Models;

namespace Romd.Admin.Application.Dashboard.Queries.GetLibrarySummary;

public sealed record GetLibrarySummaryQuery : IQuery<LibrarySummary>;

public sealed class GetLibrarySummaryQueryHandler(
    IRomRepository romRepository,
    IDatRepository datRepository,
    IFileRepository fileRepository)
    : IQueryHandler<GetLibrarySummaryQuery, LibrarySummary>
{
    public async Task<ErrorOr<LibrarySummary>> HandleAsync(GetLibrarySummaryQuery query, CancellationToken ct = default)
    {
        // Sequential awaits: the repositories share one scoped DbContext, which is single-flight
        // (ADR docs/decisions/admin-use-case-transaction-event-boundaries.md, "DbContext Concurrency").
        // Reads are per-query consistent only; cross-query consistency is not promised.
        var collectionStats = await romRepository.GetStatsAsync(ct);
        var storageStats = await fileRepository.GetStorageStatsAsync(ct);
        var dats = await datRepository.GetAllAsync(ct);

        int identifiedCount = collectionStats.TotalRomFiles - collectionStats.UnidentifiedCount;
        int routedCount = collectionStats.CatalogedCount;
        int unroutedCount = collectionStats.UnroutedCount;

        int expectedTitleCount = collectionStats.PlatformBreakdown.Sum(p => p.TotalCount);
        int localPayloadTitleCount = collectionStats.PlatformBreakdown.Sum(p => p.LocalPayloadCount);
        decimal coverageHealthPercent = expectedTitleCount > 0
            ? Math.Round((decimal)localPayloadTitleCount / expectedTitleCount * 100, 1)
            : 0m;

        var lastDatRefreshAt = dats.Count > 0
            ? dats.Max(d => d.ImportedAt)
            : (DateTimeOffset?)null;

        return new LibrarySummary
        {
            TotalRoms = collectionStats.TotalRomFiles,
            IdentifiedCount = identifiedCount,
            UnidentifiedCount = collectionStats.UnidentifiedCount,
            RoutedCount = routedCount,
            UnroutedCount = unroutedCount,
            ExpectedTitleCount = expectedTitleCount,
            LocalPayloadTitleCount = localPayloadTitleCount,
            CoverageHealthPercent = coverageHealthPercent,
            TotalStorageBytes = (ByteCount)storageStats.TotalSize,
            TotalStorageBytesOnDisk = (ByteCount)storageStats.TotalSizeOnDisk,
            CompressedFileCount = storageStats.CompressedFiles,
            UncompressedFileCount = storageStats.UncompressedFiles,
            AverageCompressionRatio = Math.Round(storageStats.AverageCompressionRatio, 3),
            BytesSaved = (ByteCount)storageStats.BytesSaved,
            LastDatRefreshAt = lastDatRefreshAt
        };
    }
}
