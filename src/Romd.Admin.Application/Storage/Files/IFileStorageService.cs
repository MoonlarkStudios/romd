using Romd.Domain.Hashing;
using Romd.Domain.Storage;

namespace Romd.Admin.Application.Storage.Files;

/// <summary>
///     The file ledger over CAS. Every file row has its blob: publication records a row only after the
///     blob is published, and deletion removes a blob only while no row claims it, both under the
///     file's mutation lock. Failures therefore leave at worst an orphan blob, which the orphan sweep reclaims.
/// </summary>
public interface IFileStorageService
{
    /// <summary>
    ///     Encodes content for a later <see cref="PublishStagedAsync(IStagedFile, CancellationToken)" /> without
    ///     locks or a transaction, so hashing and compression never run under them. The stream must stay
    ///     open and unchanged until the staged file is disposed.
    /// </summary>
    Task<IStagedFile> StageAsync(Stream content, CancellationToken ct = default);

    /// <summary>
    ///     Stages a temp file using its known hash: new content is read once, stored content not at all.
    ///     The temp file must outlive the staged file.
    /// </summary>
    Task<IStagedFile> StageAsync(ITempFile source, CancellationToken ct = default);

    /// <summary>
    ///     Stages a measured temp artifact as raw bytes, preserving its known hash and the existing
    ///     staged-file ownership lifetime. The source must outlive the staged file. Compressed objects
    ///     with the same hash are refused at staging and publication; ordinary storage policy is unchanged.
    /// </summary>
    /// <exception cref="NotSupportedException">Explicit raw staging is unsupported or the existing object is compressed.</exception>
    Task<IStagedFile> StageRawAsync(ITempFile source, CancellationToken ct = default) =>
        throw new NotSupportedException("This file storage service does not support explicit raw staging.");

    /// <summary>
    ///     Publishes bytes and stages their ledger row under the caller's transaction and file lock.
    ///     The owner calls FlushAsync only when RequiresFlush, then reads the receipt before adding
    ///     references and committing. Rollback must discard staging through the owner's unit of work.
    /// </summary>
    Task<IStagedFilePublication> PublishStagedAsync(IStagedFile file, CancellationToken ct = default);

    /// <summary>Stages a globally ordered publication batch; receipts keep input order and per-item flags.</summary>
    Task<IStagedFilePublicationBatch> PublishStagedAsync(IReadOnlyList<IStagedFile> files, CancellationToken ct = default);

    /// <summary>
    ///     Captures independently measured immutable checksums for an existing artifact inside the
    ///     caller's transaction, under its mutation lock. Conflicting identity or known hashes fail.
    /// </summary>
    Task<FileEntity> RecordArtifactChecksumsAsync(int fileId, TempFileArtifactMeasurement measurement,
        CancellationToken ct = default);

    Task<Stream?> RetrieveByIdAsync(int fileId, CancellationToken ct);
    Task<Stream?> RetrieveBySha256Async(Sha256 sha256, CancellationToken ct);
    Task<bool> ExistsAsync(int fileId, CancellationToken ct);

    /// <summary>
    ///     Deletes an unreferenced file: its row in one transaction, then its blob unless the content was
    ///     published again meanwhile. Runs outside any transaction. If only the blob deletion fails, the
    ///     file is still deleted and the orphan sweep reclaims the blob.
    /// </summary>
    Task<bool> DeleteIfUnreferencedAsync(int fileId, CancellationToken ct);

    /// <summary>
    ///     Deletes a blob that no file row claims. Safe at any time outside a transaction: it re-checks
    ///     under the file's mutation lock, which publishers hold through their row commit.
    /// </summary>
    Task<bool> DeleteOrphanAsync(Sha256 sha256, CancellationToken ct);

    Task<int> PruneOrphanedFilesAsync(CancellationToken ct);

    /// <summary>
    ///     Deletes every file that nothing references and that is older than <paramref name="minimumAge"/>,
    ///     as <see cref="DeleteIfUnreferencedAsync" /> does. Returns the number of files reclaimed.
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
