using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;
using Romd.Domain.Storage;
using Romd.Storage;

namespace Romd.Infrastructure.Storage;

public sealed class FileStorageService : IFileStorageService
{
    private readonly IContentAddressableStore _cas;
    private readonly IFileRepository _fileRepository;
    private readonly ILogger<FileStorageService> _logger;
    private readonly ContentStoreOptions _options;
    private readonly IAdminEventOutbox _outbox;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IFileMutationLock _fileLocks;

    public FileStorageService(
        IContentAddressableStore cas,
        IFileRepository fileRepository,
        IUnitOfWork unitOfWork,
        IFileMutationLock fileLocks,
        IAdminEventOutbox outbox,
        IOptions<ContentStoreOptions> options,
        ILogger<FileStorageService> logger)
    {
        _cas = cas;
        _fileRepository = fileRepository;
        _unitOfWork = unitOfWork;
        _fileLocks = fileLocks;
        _outbox = outbox;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<FileStoreResult> StoreAsync(
        Stream content,
        IProgress<StoreProgress>? progress = null,
        CancellationToken ct = default)
    {
        var storeResult = await _cas.StoreAsync(content, progress, ct);
        var sha256 = storeResult.Key.Hash;

        var existing = await _fileRepository.GetBySha256Async(sha256, ct);
        if (existing is not null)
        {
            return new FileStoreResult(existing, false, true);
        }

        var file = FileEntity.CreateNew(
            sha256,
            storeResult.Size,
            storeResult.CompressedSize,
            storeResult.IsCompressed);

        var created = await _fileRepository.AddAsync(file, ct);
        return new FileStoreResult(created, true, storeResult.WasDeduplicated);
    }

    public async Task<FileStoreResult> StoreFromTempFileAsync(ITempFile tempFile, CancellationToken ct = default)
    {
        var existing = await _fileRepository.GetBySha256Async(tempFile.FileSha256, ct);
        if (existing is not null)
        {
            var key = StorageKey.FromHash(existing.Sha256);
            if (await _cas.ExistsAsync(key, ct))
            {
                return new FileStoreResult(existing, false, true);
            }
        }

        await using var stream = tempFile.OpenRead();
        var storeResult = await _cas.StoreAsync(stream, null, ct);

        var file = await _fileRepository.GetBySha256Async(storeResult.Key.Hash, ct);
        if (file is not null)
        {
            return new FileStoreResult(file, false, true);
        }

        var created = await _fileRepository.AddAsync(FileEntity.CreateNew(
            storeResult.Key.Hash,
            storeResult.Size,
            storeResult.CompressedSize,
            storeResult.IsCompressed), ct);

        return new FileStoreResult(created, true, storeResult.WasDeduplicated);
    }

    public async Task<Stream?> RetrieveByIdAsync(int fileId, CancellationToken ct)
    {
        var file = await _fileRepository.GetByIdAsync(fileId, ct);
        if (file is null)
        {
            return null;
        }

        var key = StorageKey.FromHash(file.Sha256);
        return await _cas.RetrieveAsync(key, ct);
    }

    public async Task<Stream?> RetrieveBySha256Async(Sha256 sha256, CancellationToken ct)
    {
        var key = StorageKey.FromHash(sha256);
        return await _cas.RetrieveAsync(key, ct);
    }

    public async Task<bool> ExistsAsync(int fileId, CancellationToken ct)
    {
        var file = await _fileRepository.GetByIdAsync(fileId, ct);
        if (file is null)
        {
            return false;
        }

        return await _cas.ExistsAsync(StorageKey.FromHash(file.Sha256), ct);
    }

    public async Task<bool> DeleteIfUnreferencedAsync(int fileId, CancellationToken ct)
    {
        var file = await _fileRepository.GetByIdAsync(fileId, ct);
        if (file is null)
        {
            return false;
        }

        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);

        try
        {
            await _fileLocks.AcquireAsync(file.Sha256, ct);
            // A cleanup candidate can disappear while waiting for a publisher.
            if (await _fileRepository.GetByIdAsync(fileId, ct) is null) return false;
            if (await _fileRepository.IsReferencedAsync(fileId, ct))
            {
                return false;
            }

            await _fileRepository.DeleteStagedAsync(fileId, ct);
            await _outbox.EnqueueAsync(AdminRealtimeEventTypes.StorageStatsChanged, ct);
            await _unitOfWork.FlushAsync(ct);
            // Hold the hash lock through deletion. Committing the row first would
            // let an importer recreate its reference before this blob is removed.
            await _cas.DeleteAsync(StorageKey.FromHash(file.Sha256), ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        return true;
    }

    public async Task<int> PruneUnreferencedFilesAsync(TimeSpan minimumAge, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - minimumAge;
        var candidateIds = await _fileRepository.GetUnreferencedFileIdsOlderThanAsync(cutoff, ct);

        int reclaimed = 0;
        foreach (int fileId in candidateIds)
        {
            ct.ThrowIfCancellationRequested();

            // Re-checks the reference in a transaction before deleting the row + blob, so a file that
            // was referenced between the scan and now is safely skipped.
            if (await DeleteIfUnreferencedAsync(fileId, ct))
            {
                reclaimed++;
            }
        }

        return reclaimed;
    }

    public async Task<int> PruneOrphanedFilesAsync(CancellationToken ct)
    {
        var knownHashes = await _fileRepository.GetAllSha256HashesAsync(ct);
        var knownHashSet = knownHashes.ToHashSet();

        string root = Path.GetFullPath(_options.RootPath);
        if (!Directory.Exists(root))
        {
            return 0;
        }

        int pruned = 0;
        foreach (string filePath in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            if (!TryExtractSha256FromPath(filePath, out var sha256))
            {
                continue;
            }

            if (knownHashSet.Contains(sha256))
            {
                continue;
            }

            try
            {
                await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
                await _fileLocks.AcquireAsync(sha256, ct);
                // The initial snapshot only nominates orphans. A publisher may
                // have retained this hash after that snapshot was taken.
                if (await _fileRepository.GetBySha256Async(sha256, ct) is not null) continue;
                await _cas.DeleteAsync(StorageKey.FromHash(sha256), ct);
                await transaction.CommitAsync(ct);
                pruned++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to prune orphaned file {Hash}", sha256.ToShortHex());
            }
        }

        return pruned;
    }

    private static bool TryExtractSha256FromPath(string filePath, out Sha256 sha256)
    {
        string fileName = Path.GetFileNameWithoutExtension(filePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            sha256 = default;
            return false;
        }

        if (fileName.Length != 64)
        {
            sha256 = default;
            return false;
        }

        if (!fileName.All(c => Uri.IsHexDigit(c)))
        {
            sha256 = default;
            return false;
        }

        sha256 = Sha256.Parse(fileName.ToLowerInvariant());
        return true;
    }
}
