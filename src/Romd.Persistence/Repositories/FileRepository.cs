using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;
using Romd.Domain.Storage;
using Romd.Persistence.Entities;

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

    public async Task<FileEntity> AddAsync(FileEntity file, CancellationToken ct)
    {
        var entity = FileEntityPersistence.FromDomain(file);
        _context.Files.Add(entity);
        await _context.SaveChangesAsync(ct);
        return entity.ToDomain();
    }

    public async Task DeleteAsync(int fileId, CancellationToken ct)
    {
        await DeleteStagedAsync(fileId, ct);
        await _context.SaveChangesAsync(ct);
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
        return await _context.ReferenceAssets.AnyAsync(a => a.FileId == fileId, ct)
               || await _context.RomFiles.AnyAsync(rf => rf.FileId == fileId, ct)
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
                && !_context.ReferenceAssets.Any(a => a.FileId == f.Id)
                && !_context.RomFiles.Any(rf => rf.FileId == f.Id)
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
                _context.Files.Where(f =>
                    _context.RomFiles.Any(rf => rf.FileId == f.Id
                        && _context.DatRoms.Any(dr => dr.RomFileId == rf.Id))),
                ct),
            await AggregateCategoryAsync(
                StorageCategories.RomUnmatched,
                _context.Files.Where(f =>
                    _context.RomFiles.Any(rf => rf.FileId == f.Id)
                    && !_context.RomFiles.Any(rf => rf.FileId == f.Id
                        && _context.DatRoms.Any(dr => dr.RomFileId == rf.Id))),
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
                    && !_context.RomFiles.Any(rf => rf.FileId == f.Id)
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

    public async Task<IReadOnlyList<Sha256>> GetAllSha256HashesAsync(CancellationToken ct)
    {
        return await _context.Files
            .Select(f => f.Sha256)
            .ToListAsync(ct);
    }
}
