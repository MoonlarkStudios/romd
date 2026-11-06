using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;
using Romd.Domain.Storage;
using Romd.Storage;

namespace Romd.Infrastructure.Storage;

public sealed class FileStorageService : IFileStorageService
{
    private const int OrphanBatchSize = 1000;

    private readonly IContentAddressableStore _cas;
    private readonly IFileRepository _fileRepository;
    private readonly ILogger<FileStorageService> _logger;
    private readonly IAdminEventOutbox _outbox;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IFileMutationLock _fileLocks;
    private readonly TimeProvider _clock;

    public FileStorageService(
        IContentAddressableStore cas,
        IFileRepository fileRepository,
        IUnitOfWork unitOfWork,
        IFileMutationLock fileLocks,
        IAdminEventOutbox outbox,
        ILogger<FileStorageService> logger,
        TimeProvider clock)
    {
        _cas = cas;
        _fileRepository = fileRepository;
        _unitOfWork = unitOfWork;
        _fileLocks = fileLocks;
        _outbox = outbox;
        _logger = logger;
        _clock = clock;
    }

    public Task<IStagedFile> StageAsync(Stream content, CancellationToken ct = default) =>
        StageMeasuredAsync(content, expectedKey: null, ownedSource: null, requiresRaw: false, ct);

    public Task<IStagedFile> StageAsync(ITempFile source, CancellationToken ct = default) =>
        StageTempFileAsync(source, requiresRaw: false, ct);

    public Task<IStagedFile> StageRawAsync(ITempFile source, CancellationToken ct = default) =>
        StageTempFileAsync(source, requiresRaw: true, ct);

    private async Task<IStagedFile> StageTempFileAsync(ITempFile source, bool requiresRaw, CancellationToken ct)
    {
        var stream = source.OpenRead();
        try
        {
            return await StageMeasuredAsync(stream, StorageKey.FromHash(source.FileSha256), stream, requiresRaw, ct);
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    private async Task<IStagedFile> StageMeasuredAsync(Stream content, StorageKey? expectedKey, Stream? ownedSource,
        bool requiresRaw, CancellationToken ct)
    {
        var measured = new ArtifactHashReadStream(content);
        IStagedContent? staged = null;
        try
        {
            staged = requiresRaw
                ? await _cas.StageRawAsync(measured, expectedKey, ct: ct)
                : await _cas.StageAsync(measured, expectedKey, ct: ct);
            FileEntity? known = null;
            if (measured.Hashes is null)
            {
                known = await _fileRepository.GetBySha256Async(staged.Key.Hash, ct);
                if (known is not { ArtifactMd5: not null, ArtifactSha1: not null } || known.Size != staged.Size)
                    await measured.EnsureMeasuredAsync(staged.Size, ct);
            }
            ValidateMeasurement(measured, staged.Key.Hash, staged.Size);
            return new StagedFile(staged, measured, ownedSource, known);
        }
        catch
        {
            try
            { if (staged is not null) await staged.DisposeAsync(); }
            finally { await measured.DisposeAsync(); }
            throw;
        }
    }

    public async Task<IStagedFilePublication> PublishStagedAsync(IStagedFile file, CancellationToken ct = default) =>
        (await PublishStagedItemsAsync([file], ct)).SinglePublication();

    public async Task<IStagedFilePublicationBatch> PublishStagedAsync(IReadOnlyList<IStagedFile> files,
        CancellationToken ct = default) => await PublishStagedItemsAsync(files, ct);

    private async Task<StagedPublications> PublishStagedItemsAsync(IReadOnlyList<IStagedFile> files, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var boundary = _fileLocks.CapturePublicationBoundary();
        var staged = await LockPublicationFilesAsync(files, ct);
        var publications = new List<IStagedFilePublication>(staged.Count);
        foreach (var file in staged)
            publications.Add(new GuardedPublication(await RecordStagedAsync(await file.Content.PublishAsync(ct), file, ct), boundary));
        return new StagedPublications(publications.ToArray(), boundary);
    }

    private async Task<List<StagedFile>> LockPublicationFilesAsync(IReadOnlyList<IStagedFile> files, CancellationToken ct)
    {
        var staged = files.Select(file => file as StagedFile
            ?? throw new ArgumentException("Staged files must come from this service.", nameof(files))).ToList();
        // The caller's transaction holds each lock until it ends. One global order prevents deadlock.
        foreach (var hash in staged.Select(file => file.Sha256).Distinct()
                     .OrderBy(hash => hash.ToString(), StringComparer.Ordinal))
            await _fileLocks.AcquireAsync(hash, ct);

        return staged;
    }

    private sealed class ExistingPublication(FileStoreResult result) : IStagedFilePublication
    {
        public bool RequiresFlush => false;
        public FileStoreResult ReadAfterFlush() => result;
    }

    private sealed class PendingPublication(IStagedFileCreation creation, bool wasNew, bool wasDeduplicated) : IStagedFilePublication
    {
        public bool RequiresFlush => creation.RequiresFlush;
        public FileStoreResult ReadAfterFlush() => new(creation.ReadAfterFlush(), wasNew, wasDeduplicated);
    }

    private sealed class GuardedPublication(IStagedFilePublication publication, IFilePublicationBoundary boundary) : IStagedFilePublication
    {
        public bool RequiresFlush { get { boundary.EnsureActive(); return publication.RequiresFlush; } }
        public FileStoreResult ReadAfterFlush() { boundary.EnsureActive(); return publication.ReadAfterFlush(); }
    }

    private sealed class StagedPublications(IStagedFilePublication[] publications, IFilePublicationBoundary boundary) : IStagedFilePublicationBatch
    {
        public bool RequiresFlush { get { boundary.EnsureActive(); return publications.Any(publication => publication.RequiresFlush); } }
        public IReadOnlyList<FileStoreResult> ReadAfterFlush()
        { boundary.EnsureActive(); return publications.Select(publication => publication.ReadAfterFlush()).ToArray(); }
        public IStagedFilePublication SinglePublication() { boundary.EnsureActive(); return publications.Single(); }
    }

    public async Task<FileEntity> RecordArtifactChecksumsAsync(int fileId, TempFileArtifactMeasurement measurement,
        CancellationToken ct = default)
    {
        await _fileLocks.AcquireAsync(measurement.Sha256, ct);
        return await _fileRepository.RecordArtifactChecksumsAsync(fileId, measurement.Sha256, measurement.Size,
            measurement.Md5, measurement.Sha1, ct);
    }

    private async Task<IStagedFilePublication> RecordStagedAsync(StoreResult stored, StagedFile staged, CancellationToken ct)
    {
        ValidateMeasurement(staged.Measured, stored.Key.Hash, stored.Size);
        var pending = await _fileRepository.FindPendingCreationAsync(stored.Key.Hash, ct);
        if (pending is not null)
        {
            ValidateIdentity(stored, staged, pending.Facts.Size, pending.Facts.ArtifactMd5, pending.Facts.ArtifactSha1);
            var creation = pending.Creation;
            if (pending.Facts.ArtifactMd5 is null && staged.ArtifactMd5 is not null ||
                pending.Facts.ArtifactSha1 is null && staged.ArtifactSha1 is not null)
                creation = await _fileRepository.AddStagedAsync(NewFile(stored, staged, pending.Facts.CreatedAt), ct);
            return new PendingPublication(creation, false, true);
        }
        var existing = await GetRecordedAsync(stored, staged, ct);
        if (existing is not null)
            return new ExistingPublication(new FileStoreResult(existing, false, true));
        var created = await _fileRepository.AddStagedAsync(NewFile(stored, staged, _clock.GetUtcNow()), ct);
        return new PendingPublication(created, true, stored.WasDeduplicated);
    }

    private async Task<FileEntity?> GetRecordedAsync(StoreResult stored, StagedFile staged, CancellationToken ct)
    {
        var existing = await _fileRepository.GetBySha256Async(stored.Key.Hash, ct);
        if (existing is null)
            return null;
        ValidateIdentity(stored, staged, existing.Size, existing.ArtifactMd5, existing.ArtifactSha1);
        if (staged.ArtifactMd5 is { } md5 && staged.ArtifactSha1 is { } sha1 &&
            (existing.ArtifactMd5 is null || existing.ArtifactSha1 is null))
            existing = await _fileRepository.RecordArtifactChecksumsAsync(existing.Id, stored.Key.Hash, stored.Size, md5, sha1, ct);
        return existing;
    }

    private static void ValidateIdentity(StoreResult stored, StagedFile staged, long size, Md5? md5, Sha1? sha1)
    {
        if (size != stored.Size || staged.ArtifactMd5 is { } measuredMd5 && md5 is { } knownMd5 && measuredMd5 != knownMd5 ||
            staged.ArtifactSha1 is { } measuredSha1 && sha1 is { } knownSha1 && measuredSha1 != knownSha1)
            throw new InvalidDataException("Measured artifact checksums conflict with the file ledger.");
    }

    private static FileEntity NewFile(StoreResult stored, StagedFile staged, DateTimeOffset createdAt) =>
        FileEntity.CreateNew(stored.Key.Hash, stored.Size, stored.CompressedSize, stored.IsCompressed, createdAt,
            staged.ArtifactMd5, staged.ArtifactSha1);

    private static void ValidateMeasurement(ArtifactHashReadStream measured, Sha256 sha256, long size)
    {
        if (measured.Hashes is { } hashes && (hashes.Sha256 != sha256 || measured.MeasuredSize != size))
            throw new InvalidDataException("Measured artifact identity differs from the staged content.");
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
        if (file is null || !await DeleteRowIfUnreferencedAsync(fileId, file.Sha256, ct))
        {
            return false;
        }

        try
        {
            await DeleteOrphanAsync(file.Sha256, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The committed row deletion made the blob an orphan, which the orphan sweep reclaims.
            _logger.LogWarning(ex, "Deleted file {FileId} but not its blob; the orphan sweep will reclaim it", fileId);
        }

        return true;
    }

    private async Task<bool> DeleteRowIfUnreferencedAsync(int fileId, Sha256 sha256, CancellationToken ct)
    {
        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);

        try
        {
            await _fileLocks.AcquireAsync(sha256, ct);
            // A cleanup candidate can disappear, or gain a reference, while waiting for a publisher.
            if (await _fileRepository.GetByIdAsync(fileId, ct) is null)
                return false;
            if (await _fileRepository.IsReferencedAsync(fileId, ct))
            {
                return false;
            }

            await _fileRepository.DeleteStagedAsync(fileId, ct);
            await _outbox.EnqueueAsync(AdminRealtimeEventTypes.StorageStatsChanged, ct);
            await _unitOfWork.FlushAsync(ct);
            await transaction.CommitAsync(ct);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<bool> DeleteOrphanAsync(Sha256 sha256, CancellationToken ct)
    {
        await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
        await _fileLocks.AcquireAsync(sha256, ct);
        // Publishers hold this lock through their row commit: with no row now, none can claim the blob
        // before this transaction ends.
        if (await _fileRepository.ExistsBySha256Async(sha256, ct))
            return false;
        bool deleted = await _cas.DeleteAsync(StorageKey.FromHash(sha256), ct);
        await transaction.CommitAsync(ct);
        return deleted;
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
        int pruned = 0;
        // The inventory streams and rows are checked a batch at a time, so memory stays bounded.
        await foreach (var batch in _cas.EnumerateAsync(ct).Chunk(OrphanBatchSize).WithCancellation(ct))
        {
            var recorded = await _fileRepository.GetRecordedAsync(batch.Select(key => key.Hash).ToList(), ct);
            foreach (var key in batch.Where(key => !recorded.Contains(key.Hash)))
            {
                try
                {
                    // The batch only nominates orphans. A publisher may have recorded this hash since.
                    if (await DeleteOrphanAsync(key.Hash, ct))
                        pruned++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to prune orphaned file {Hash}", key.Hash.ToShortHex());
                }
            }
        }

        return pruned;
    }

    /// <summary>Owns the staged content and, for a temp file, the stream publication may reread.</summary>
    private sealed class StagedFile(IStagedContent content, ArtifactHashReadStream measured, Stream? source,
        FileEntity? known) : IStagedFile
    {
        public IStagedContent Content { get; } = content;
        public ArtifactHashReadStream Measured { get; } = measured;
        public Sha256 Sha256 => Content.Key.Hash;
        public long Size => Content.Size;
        public Md5? ArtifactMd5 => Measured.Hashes?.Md5 ?? known?.ArtifactMd5;
        public Sha1? ArtifactSha1 => Measured.Hashes?.Sha1 ?? known?.ArtifactSha1;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Content.DisposeAsync();
            }
            finally
            {
                try
                { await Measured.DisposeAsync(); }
                finally { if (source is not null) await source.DisposeAsync(); }
            }
        }
    }
}
