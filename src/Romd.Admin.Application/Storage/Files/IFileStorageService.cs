using Romd.Domain.Hashing;
using Romd.Domain.Storage;
using Romd.Storage;

namespace Romd.Admin.Application.Storage.Files;

public interface IFileStorageService
{
    Task<FileStoreResult> StoreAsync(
        Stream content,
        IProgress<StoreProgress>? progress = null,
        CancellationToken ct = default);

    Task<FileStoreResult> StoreFromTempFileAsync(
        ITempFile tempFile,
        CancellationToken ct = default);

    Task<Stream?> RetrieveByIdAsync(int fileId, CancellationToken ct);
    Task<Stream?> RetrieveBySha256Async(Sha256 sha256, CancellationToken ct);
    Task<bool> ExistsAsync(int fileId, CancellationToken ct);

    Task<bool> DeleteIfUnreferencedAsync(int fileId, CancellationToken ct);
    Task<int> PruneOrphanedFilesAsync(CancellationToken ct);

    /// <summary>
    ///     Deletes the row and CAS blob of every file that nothing references and that is older than
    ///     <paramref name="minimumAge"/>. The age bound protects in-flight uploads (a blob is stored
    ///     a moment before its owning row commits). Returns the number of files reclaimed.
    /// </summary>
    Task<int> PruneUnreferencedFilesAsync(TimeSpan minimumAge, CancellationToken ct);
}

public sealed record FileStoreResult(
    FileEntity File,
    bool WasNew,
    bool WasDeduplicated);

public sealed record FileStorageStats(
    int TotalFiles,
    long TotalSize,
    long TotalSizeOnDisk,
    int CompressedFiles,
    int UncompressedFiles,
    IReadOnlyList<StorageCategoryStats> Breakdown)
{
    public long BytesSaved => TotalSize - TotalSizeOnDisk;
    public double AverageCompressionRatio => TotalSize > 0 ? (double)TotalSizeOnDisk / TotalSize : 1.0;
}

/// <summary>
///     CAS byte attribution for one owner category (ROM matched/unmatched, DAT, media, unattributed).
///     Categories are not guaranteed disjoint — content-addressed dedup means a single blob can be
///     referenced by more than one owner — so the breakdown is attribution, not a partition of the total.
/// </summary>
public sealed record StorageCategoryStats(
    string Category,
    int FileCount,
    long Size,
    long SizeOnDisk);

/// <summary>
///     Stable category keys for <see cref="StorageCategoryStats.Category" />.
/// </summary>
public static class StorageCategories
{
    public const string RomMatched = "rom_matched";
    public const string RomUnmatched = "rom_unmatched";
    public const string Dat = "dat";
    public const string Media = "media";
    public const string Unattributed = "unattributed";
}
