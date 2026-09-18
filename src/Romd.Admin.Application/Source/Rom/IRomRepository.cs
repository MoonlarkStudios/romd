using Romd.Application.Common.Pagination;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;

namespace Romd.Admin.Application.Source.Rom;

public interface IRomRepository
{
    Task<IReadOnlyList<RomMatchData>> GetMatchesAsync(int romId, CancellationToken cancellationToken = default);
    Task<RomFile?> GetByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<RomFile?> GetBySha1Async(Sha1 sha1, CancellationToken cancellationToken = default);

    Task<RomFile?> GetByHashAsync(
        Sha1? sha1 = null,
        Md5? md5 = null,
        Crc32? crc32 = null,
        long? size = null,
        CancellationToken cancellationToken = default);

    Task<RomFile> AddAsync(RomFile romFile, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Stages a ROM row without saving. The caller owns the flush and commit.
    /// </summary>
    Task AddStagedAsync(RomFile romFile, CancellationToken cancellationToken = default);

    Task DeleteAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Bulk-deletes every unidentified ROM (no DAT match — the inbox). Returns the number
    ///     removed; orphaned CAS files are reclaimed by the recurring cleanup job.
    /// </summary>
    Task<int> DeleteUnidentifiedAsync(CancellationToken cancellationToken = default);
    Task<bool> ExistsBySha1Async(Sha1 sha1, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RomFile>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RomFile>> GetOrphanedAsync(CancellationToken cancellationToken = default);

    Task<PagedList<RomFile>> GetByStatusAsync(
        RomCatalogStatus status,
        string? cursor = null,
        int limit = 50,
        CancellationToken cancellationToken = default);

    Task<CollectionStats> GetStatsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PlatformBreakdown>> GetPlatformBreakdownAsync(
        CancellationToken cancellationToken = default);
    Task<CoverageBreakdown> GetCoverageBreakdownAsync(CancellationToken cancellationToken = default);
    Task<RomCatalogStatus> GetStatusAsync(int romFileId, CancellationToken cancellationToken = default);
    Task<bool> IsAccessibleAsync(int romFileId, int? libraryId, CancellationToken cancellationToken = default);

    Task<PagedList<RomWithStatus>> ListAsync(
        RomCatalogStatus? status = null,
        string? cursor = null,
        int limit = 50,
        CancellationToken cancellationToken = default,
        string? search = null);
}
