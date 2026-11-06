using ErrorOr;
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

    Task LockRepresentationSetAsync(int romId, CancellationToken ct = default);

    Task<IReadOnlyList<RetainedRepresentation>> ListRepresentationsAsync(int romId, CancellationToken ct = default);

    /// <summary>Switches preference to an owned artifact within the caller's locked transaction.</summary>
    Task SetPreferredRepresentationStagedAsync(int romId, int outputId, CancellationToken ct = default);

    /// <summary>Releases owned artifacts and their evidence, retaining file rows for ledger cleanup.</summary>
    Task ReleaseRepresentationsStagedAsync(int romId, IReadOnlyList<int> outputIds, CancellationToken ct = default);

    /// <summary>Retains one whole-file measurement under the caller's logical/file locks. Returns the exact retained proof identity.</summary>
    Task<RomRetainedMeasurement?> RecordWholeMeasurementStagedAsync(int romId, int? publishedFileId, RomContainerFacts container,
        RomRepresentationVerification verification, CancellationToken ct = default);

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

public sealed record RomRetainedMeasurement(int RepresentationId, int OutputId, int FileId, Guid VerificationId);
