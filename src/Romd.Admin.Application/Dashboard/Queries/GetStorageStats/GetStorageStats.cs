using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Storage.Files;
using Romd.Contracts.Common.Models;
using Romd.Contracts.Management.Models;

namespace Romd.Admin.Application.Dashboard.Queries.GetStorageStats;

public sealed record GetStorageStatsQuery : IQuery<StorageStatsDto>;

public sealed class GetStorageStatsQueryHandler(IFileRepository fileRepository)
    : IQueryHandler<GetStorageStatsQuery, StorageStatsDto>
{
    public async Task<ErrorOr<StorageStatsDto>> HandleAsync(GetStorageStatsQuery query, CancellationToken ct = default)
    {
        var stats = await fileRepository.GetStorageStatsAsync(ct);

        return new StorageStatsDto
        {
            TotalStorageBytes = (ByteCount)stats.TotalSize,
            TotalStorageBytesOnDisk = (ByteCount)stats.TotalSizeOnDisk,
            CompressedFileCount = stats.CompressedFiles,
            UncompressedFileCount = stats.UncompressedFiles,
            AverageCompressionRatio = Math.Round(stats.AverageCompressionRatio, 3),
            BytesSaved = (ByteCount)stats.BytesSaved,
            Breakdown = stats.Breakdown
                .Select(c => new StorageCategoryDto
                {
                    Category = c.Category,
                    FileCount = c.FileCount,
                    SizeBytes = (ByteCount)c.Size,
                    SizeOnDiskBytes = (ByteCount)c.SizeOnDisk
                })
                .ToList()
        };
    }
}
