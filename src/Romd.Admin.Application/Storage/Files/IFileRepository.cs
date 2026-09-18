using Romd.Domain.Hashing;
using Romd.Domain.Storage;

namespace Romd.Admin.Application.Storage.Files;

public interface IFileRepository
{
    Task<FileEntity?> GetByIdAsync(int id, CancellationToken ct);
    Task<FileEntity?> GetBySha256Async(Sha256 sha256, CancellationToken ct);
    Task<FileEntity> AddAsync(FileEntity file, CancellationToken ct);
    Task DeleteAsync(int fileId, CancellationToken ct);

    /// <summary>
    ///     Stages a file-row deletion without saving. The caller owns the flush and commit.
    /// </summary>
    Task DeleteStagedAsync(int fileId, CancellationToken ct);

    Task<bool> ExistsBySha256Async(Sha256 sha256, CancellationToken ct);

    Task<bool> IsReferencedAsync(int fileId, CancellationToken ct);

    /// <summary>
    ///     Ids of files that nothing references (no RomFile, DatFile, or TitleMedia) and were created
    ///     before <paramref name="createdBefore"/>. The age bound avoids deleting a file that an
    ///     in-flight upload has stored but not yet linked.
    /// </summary>
    Task<IReadOnlyList<int>> GetUnreferencedFileIdsOlderThanAsync(
        DateTimeOffset createdBefore,
        CancellationToken ct);

    Task<FileStorageStats> GetStorageStatsAsync(CancellationToken ct);

    Task<IReadOnlyList<Sha256>> GetAllSha256HashesAsync(CancellationToken ct);
}
