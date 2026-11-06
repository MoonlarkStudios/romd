using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;
using Romd.Domain.Storage;
using Romd.Persistence.Entities;
using Romd.Persistence.Queries;

namespace Romd.Persistence.Repositories;

public sealed class FileRepository : IFileRepository
{
    private readonly RomdDbContext _context;

    public FileRepository(RomdDbContext context)
    {
        _context = context;
    }

    public async Task<FileEntity?> GetByIdAsync(int id, CancellationToken ct)
    {
        var entity = await _context.Files.FirstOrDefaultAsync(f => f.Id == id, ct);
        return entity?.ToDomain();
    }

    public async Task<FileEntity?> GetBySha256Async(Sha256 sha256, CancellationToken ct)
    {
        var entity = await _context.Files.FirstOrDefaultAsync(f => f.Sha256 == sha256, ct);
        return entity?.ToDomain();
    }

    public Task<IStagedFileCreation> AddStagedAsync(FileEntity file, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (file.Id != 0)
            throw new ArgumentException("File creation requires a new file without an identity.", nameof(file));
        var transaction = _context.Database.CurrentTransaction ?? throw new InvalidOperationException(
            "File creation requires the caller's publication transaction.");
        var entity = FindPendingEntity(file.Sha256);
        if (entity is null)
        {
            entity = FileEntityPersistence.FromDomain(file);
            _context.Files.Add(entity);
        }
        else
            MergePendingChecksums(entity, file);
        return Task.FromResult<IStagedFileCreation>(new StagedFileCreation(_context, _context.Entry(entity), transaction.TransactionId));
    }

    public Task<PendingFileCreation?> FindPendingCreationAsync(Sha256 sha256, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var transaction = _context.Database.CurrentTransaction ?? throw new InvalidOperationException(
            "File publication requires the caller's transaction.");
        var entity = FindPendingEntity(sha256);
        return Task.FromResult(entity is null ? null : new PendingFileCreation(
            new FilePublicationFacts(entity.Sha256, entity.Size, entity.SizeOnDisk, entity.IsCompressed,
                entity.CreatedAt, entity.ArtifactMd5, entity.ArtifactSha1),
            new StagedFileCreation(_context, _context.Entry(entity), transaction.TransactionId)));
    }

    private FileEntityPersistence? FindPendingEntity(Sha256 sha256) =>
        _context.Database.CurrentTransaction is null ? null : _context.ChangeTracker.Entries<FileEntityPersistence>()
            .Where(entry => entry.State == EntityState.Added && entry.Entity.Sha256 == sha256)
            .Select(entry => entry.Entity).SingleOrDefault();

    private static void MergePendingChecksums(FileEntityPersistence entity, FileEntity file)
    {
        if (entity.Size != file.Size || entity.ArtifactMd5 is { } md5 && file.ArtifactMd5 is { } incomingMd5 && md5 != incomingMd5 ||
            entity.ArtifactSha1 is { } sha1 && file.ArtifactSha1 is { } incomingSha1 && sha1 != incomingSha1)
            throw new InvalidDataException("Measured artifact checksums conflict with the file ledger.");
        entity.ArtifactMd5 ??= file.ArtifactMd5;
        entity.ArtifactSha1 ??= file.ArtifactSha1;
    }

    private sealed class StagedFileCreation(RomdDbContext context, EntityEntry<FileEntityPersistence> entry,
        Guid transactionId) : IStagedFileCreation
    {
        public bool RequiresFlush { get { RequireBoundary(); return entry.State == EntityState.Added; } }
        public FileEntity ReadAfterFlush()
        {
            RequireBoundary();
            return entry.State != EntityState.Added && entry.Entity.Id > 0 ? entry.Entity.ToDomain()
                : throw new InvalidOperationException("Flush staged file creation before reading its identity.");
        }
        private void RequireBoundary()
        {
            if (context.Database.CurrentTransaction?.TransactionId != transactionId ||
                entry.State is EntityState.Detached or EntityState.Deleted)
                throw new InvalidOperationException("File creation receipts belong to their active publication transaction.");
        }
    }

    public async Task<FileEntity> RecordArtifactChecksumsAsync(int fileId, Sha256 sha256, long size, Md5 md5, Sha1 sha1,
        CancellationToken ct)
    {
        if (_context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Capturing artifact checksums requires the publication transaction.");
        int updated = await _context.Files
            .Where(file => file.Id == fileId && file.Sha256 == sha256 && file.Size == size
                && (file.ArtifactMd5 == null || file.ArtifactMd5 == md5)
                && (file.ArtifactSha1 == null || file.ArtifactSha1 == sha1))
            .ExecuteUpdateAsync(set => set
                .SetProperty(file => file.ArtifactMd5, file => (Md5?)(file.ArtifactMd5 ?? md5))
                .SetProperty(file => file.ArtifactSha1, file => (Sha1?)(file.ArtifactSha1 ?? sha1)), ct);
        if (updated != 1)
            throw new InvalidDataException("Measured artifact checksums conflict with the file ledger.");

        var entity = await _context.Files.AsNoTracking().SingleAsync(file => file.Id == fileId, ct);
        // Rebase only the captured facts; flushing other tracked mutations belongs to the publication owner.
        foreach (var tracked in _context.ChangeTracker.Entries<FileEntityPersistence>()
                     .Where(entry => entry.Entity.Id == fileId).ToArray())
        {
            var md5Property = tracked.Property(file => file.ArtifactMd5);
            md5Property.CurrentValue = md5Property.OriginalValue = entity.ArtifactMd5;
            md5Property.IsModified = false;
            var sha1Property = tracked.Property(file => file.ArtifactSha1);
            sha1Property.CurrentValue = sha1Property.OriginalValue = entity.ArtifactSha1;
            sha1Property.IsModified = false;
        }
        return entity.ToDomain();
    }

    public async Task DeleteStagedAsync(int fileId, CancellationToken ct)
    {
        var entity = await _context.Files.AsTracking().FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (entity is null)
        {
            return;
        }

        _context.Files.Remove(entity);
    }

    public Task<bool> ExistsBySha256Async(Sha256 sha256, CancellationToken ct) =>
        _context.Files.AnyAsync(f => f.Sha256 == sha256, ct);

    public async Task<bool> IsReferencedAsync(int fileId, CancellationToken ct)
    {
        return await _context.OriginalDiscLayouts.AnyAsync(x => x.DescriptorFileId == fileId, ct)
               || await _context.OriginalDiscLayoutFiles.AnyAsync(x => x.FileId == fileId, ct)
               || await _context.ReferenceAssets.AnyAsync(a => a.FileId == fileId, ct)
               || await _context.RomRepresentations.AnyAsync(rep => rep.FileId == fileId, ct)
               || await _context.ReconstructionMembers.AnyAsync(member => member.StoredFileId == fileId, ct)
               || await _context.Set<MediaReconciliationPinEntity>().AnyAsync(pin => pin.FileId == fileId, ct)
               || await _context.DatFiles.AnyAsync(df => df.FileId == fileId, ct)
               || await _context.DatSubscriptions.AnyAsync(s => s.CandidateFileId == fileId, ct)
               || await _context.TitleMedia.AnyAsync(tm => tm.FileId == fileId, ct)
               || await _context.ArtworkAssets.AnyAsync(a => a.OriginalFileId == fileId, ct)
               || await _context.ArtworkVariants.AnyAsync(v => v.FileId == fileId, ct);
    }

    public async Task<IReadOnlyList<int>> GetUnreferencedFileIdsOlderThanAsync(
        DateTimeOffset createdBefore,
        CancellationToken ct)
    {
        // Same reference set as IsReferencedAsync, including artwork originals and variants.
        return await _context.Files
            .Where(f => f.CreatedAt < createdBefore
                && !_context.OriginalDiscLayouts.Any(x => x.DescriptorFileId == f.Id)
                && !_context.OriginalDiscLayoutFiles.Any(x => x.FileId == f.Id)
                && !_context.ReferenceAssets.Any(a => a.FileId == f.Id)
                && !_context.RomRepresentations.Any(rep => rep.FileId == f.Id)
                && !_context.ReconstructionMembers.Any(member => member.StoredFileId == f.Id)
                && !_context.Set<MediaReconciliationPinEntity>().Any(pin => pin.FileId == f.Id)
                && !_context.DatFiles.Any(df => df.FileId == f.Id)
                && !_context.DatSubscriptions.Any(s => s.CandidateFileId == f.Id)
                && !_context.TitleMedia.Any(tm => tm.FileId == f.Id)
                && !_context.ArtworkAssets.Any(a => a.OriginalFileId == f.Id)
                && !_context.ArtworkVariants.Any(v => v.FileId == f.Id))
            .Select(f => f.Id)
            .ToListAsync(ct);
    }

    public async Task<FileStorageStats> GetStorageStatsAsync(CancellationToken ct)
    {
        int totalFiles = await _context.Files.CountAsync(ct);
        long totalSize = await _context.Files.SumAsync(f => (long?)f.Size, ct) ?? 0L;
        long totalSizeOnDisk = await _context.Files.SumAsync(f => (long?)f.SizeOnDisk, ct) ?? 0L;
        int compressedFiles = await _context.Files.CountAsync(f => f.IsCompressed, ct);
        int uncompressedFiles = totalFiles - compressedFiles;

        var breakdown = await BuildStorageBreakdownAsync(ct);

        return new FileStorageStats(
            totalFiles,
            totalSize,
            totalSizeOnDisk,
            compressedFiles,
            uncompressedFiles,
            breakdown);
    }

    /// <summary>
    ///     Attributes CAS bytes to owner categories via the same reference tables as
    ///     <see cref="IsReferencedAsync" />. ROMs split into matched (a DatRom points at the RomFile)
    ///     vs unmatched (content we hold but can't place — e.g. CHDs, unrecognized dumps), plus DAT,
    ///     media, and a true-orphan "unattributed" bucket. Categories may overlap under dedup.
    /// </summary>
    private async Task<IReadOnlyList<StorageCategoryStats>> BuildStorageBreakdownAsync(CancellationToken ct)
    {
        return
        [
            await AggregateCategoryAsync(
                StorageCategories.RomMatched,
                _context.Files.Where(file =>
                    _context.RomRepresentations.Any(graph => graph.FileId == file.Id &&
                        _context.RepresentationOutputs.Any(output => output.RepresentationId == graph.Id &&
                            _context.DatRoms.Any(dat => dat.RomFileId == output.RomId))) ||
                    _context.CatalogMatchedPinnedFileIds().Contains(file.Id)),
                ct),
            await AggregateCategoryAsync(
                StorageCategories.RomUnmatched,
                _context.Files.Where(file =>
                    (_context.RomRepresentations.Any(graph => graph.FileId == file.Id) ||
                        _context.ReconstructionMembers.Any(member => member.StoredFileId == file.Id) ||
                        _context.OriginalDiscLayouts.Any(layout => layout.DescriptorFileId == file.Id) ||
                        _context.OriginalDiscLayoutFiles.Any(member => member.FileId == file.Id) ||
                        _context.Set<MediaReconciliationPinEntity>().Any(pin => pin.FileId == file.Id)) &&
                    !_context.RomRepresentations.Any(graph => graph.FileId == file.Id &&
                        _context.RepresentationOutputs.Any(output => output.RepresentationId == graph.Id &&
                            _context.DatRoms.Any(dat => dat.RomFileId == output.RomId))) &&
                    !_context.CatalogMatchedPinnedFileIds().Contains(file.Id)),
                ct),
            await AggregateCategoryAsync(
                StorageCategories.Dat,
                _context.Files.Where(f => _context.DatFiles.Any(df => df.FileId == f.Id)
                    || _context.DatSubscriptions.Any(s => s.CandidateFileId == f.Id)),
                ct),
            await AggregateCategoryAsync(
                StorageCategories.Media,
                _context.Files.Where(f => _context.ReferenceAssets.Any(a => a.FileId == f.Id)
                    || _context.TitleMedia.Any(tm => tm.FileId == f.Id)
                    || _context.ArtworkAssets.Any(a => a.OriginalFileId == f.Id)
                    || _context.ArtworkVariants.Any(v => v.FileId == f.Id)),
                ct),
            await AggregateCategoryAsync(
                StorageCategories.Unattributed,
                _context.Files.Where(f =>
                    !_context.ReferenceAssets.Any(a => a.FileId == f.Id)
                    && !_context.OriginalDiscLayouts.Any(layout => layout.DescriptorFileId == f.Id)
                    && !_context.OriginalDiscLayoutFiles.Any(member => member.FileId == f.Id)
                    && !_context.RomRepresentations.Any(rep => rep.FileId == f.Id)
                    && !_context.ReconstructionMembers.Any(member => member.StoredFileId == f.Id)
                    && !_context.Set<MediaReconciliationPinEntity>().Any(pin => pin.FileId == f.Id)
                    && !_context.DatFiles.Any(df => df.FileId == f.Id)
                    && !_context.DatSubscriptions.Any(s => s.CandidateFileId == f.Id)
                    && !_context.TitleMedia.Any(tm => tm.FileId == f.Id)
                    && !_context.ArtworkAssets.Any(a => a.OriginalFileId == f.Id)
                    && !_context.ArtworkVariants.Any(v => v.FileId == f.Id)),
                ct),
        ];
    }

    private static async Task<StorageCategoryStats> AggregateCategoryAsync(
        string category,
        IQueryable<FileEntityPersistence> files,
        CancellationToken ct)
    {
        var agg = await files
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Count = g.Count(),
                Size = g.Sum(f => f.Size),
                SizeOnDisk = g.Sum(f => f.SizeOnDisk)
            })
            .FirstOrDefaultAsync(ct);

        return new StorageCategoryStats(category, agg?.Count ?? 0, agg?.Size ?? 0L, agg?.SizeOnDisk ?? 0L);
    }

    public async Task<IReadOnlySet<Sha256>> GetRecordedAsync(IReadOnlyCollection<Sha256> hashes, CancellationToken ct)
    {
        var recorded = await _context.Files
            .Where(f => hashes.Contains(f.Sha256))
            .Select(f => f.Sha256)
            .ToListAsync(ct);
        return recorded.ToHashSet();
    }
}
