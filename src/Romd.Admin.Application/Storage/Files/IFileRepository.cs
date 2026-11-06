using Romd.Domain.Hashing;
using Romd.Domain.Storage;

namespace Romd.Admin.Application.Storage.Files;

public interface IFileRepository
{
    Task<FileEntity?> GetByIdAsync(int id, CancellationToken ct);
    Task<FileEntity?> GetBySha256Async(Sha256 sha256, CancellationToken ct);
    /// <summary>Stages an identity-free file in the caller's transaction under its file mutation lock.</summary>
    Task<IStagedFileCreation> AddStagedAsync(FileEntity file, CancellationToken ct);

    /// <summary>Finds a live tracked insertion for this hash in the caller's publication transaction.</summary>
    Task<PendingFileCreation?> FindPendingCreationAsync(Sha256 sha256, CancellationToken ct);

    /// <summary>Fills unknown artifact hashes from measured bytes under the file mutation lock.</summary>
    Task<FileEntity> RecordArtifactChecksumsAsync(int fileId, Sha256 sha256, long size, Md5 md5, Sha1 sha1,
        CancellationToken ct);

    /// <summary>
    ///     Stages a file-row deletion without saving. The caller owns the flush and commit.
    /// </summary>
    Task DeleteStagedAsync(int fileId, CancellationToken ct);

    Task<bool> ExistsBySha256Async(Sha256 sha256, CancellationToken ct);

    Task<bool> IsReferencedAsync(int fileId, CancellationToken ct);

    /// <summary>
    ///     Ids of files that nothing references (no RomFile, DatFile, or TitleMedia) and were created
    ///     before <paramref name="createdBefore"/>.
    /// </summary>
    Task<IReadOnlyList<int>> GetUnreferencedFileIdsOlderThanAsync(
        DateTimeOffset createdBefore,
        CancellationToken ct);

    Task<FileStorageStats> GetStorageStatsAsync(CancellationToken ct);

    /// <summary>Returns the subset of <paramref name="hashes" /> that have a file row.</summary>
    Task<IReadOnlySet<Sha256>> GetRecordedAsync(IReadOnlyCollection<Sha256> hashes, CancellationToken ct);
}
