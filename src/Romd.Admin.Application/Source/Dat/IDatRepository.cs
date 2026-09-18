using Romd.Application.Common.Pagination;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Dat;

namespace Romd.Admin.Application.Source.Dat;

public interface IDatRepository
{
    Task<DatFile?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets the live (Active or PendingActivation) version referencing the stored file.
    ///     Superseded provenance rows are ignored so retained history never trips duplicate
    ///     detection: re-uploading superseded content is the legitimate rollback path.
    /// </summary>
    Task<DatFile?> GetByFileIdAsync(int fileId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets the source's current Active version, if any.
    /// </summary>
    Task<DatFile?> GetActiveBySourceIdAsync(int datSourceId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DatFile>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets a DAT by id together with the size of its stored source file (left-joined from CAS).
    /// </summary>
    Task<DatWithSize?> GetByIdWithSizeAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets all DATs together with the size of each stored source file (left-joined from CAS).
    /// </summary>
    Task<IReadOnlyList<DatWithSize>> GetAllWithSizeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets a platform's Active DATs with their catalog source status and id, joined in a
    ///     single query snapshot so concurrent deletions never surface torn rows.
    /// </summary>
    Task<IReadOnlyList<DatWithSourceStatus>> GetByPlatformIdAsync(
        int platformId,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets all unrouted Active DATs with their catalog source status and id, joined in a
    ///     single query snapshot so concurrent deletions never surface torn rows.
    /// </summary>
    Task<IReadOnlyList<DatWithSourceStatus>> GetUnroutedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Counts distinct stored ROM files matched to each of the given DATs.
    ///     DATs with no matched files are absent from the result.
    /// </summary>
    Task<IReadOnlyDictionary<int, int>> CountMatchedRomFilesByDatAsync(
        IReadOnlyList<int> datIds,
        CancellationToken cancellationToken = default);
    Task<DatFile> AddAsync(DatFile datFile, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Stages a DAT row and its new source anchor without saving. The caller owns the
    ///     flush and commit; the generated source id is fixed up onto the DAT row at flush.
    /// </summary>
    Task AddStagedAsync(DatFile datFile, DatSource source, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Stages a new version row for an existing source without saving. The caller owns
    ///     the flush and commit; the partial unique indexes enforce lifecycle exclusivity.
    /// </summary>
    Task AddVersionStagedAsync(DatFile datFile, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Stages the version's lifecycle transition (Lifecycle and SupersededAt) without
    ///     saving. The caller owns the flush and commit.
    /// </summary>
    Task UpdateLifecycleStagedAsync(DatFile datFile, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Deletes the version's parsed game graph within the caller's ambient transaction.
    ///     Provider payload child rows cascade at the database; catalog projection provenance
    ///     converges after the caller marks the affected platform Dirty. The DatFile row itself
    ///     is retained.
    /// </summary>
    Task DeleteGamesByDatFileIdAsync(int datFileId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Retention N=1: deletes every Superseded version of the source except the most
    ///     recently superseded one. Idempotent and safely re-runnable; deleted rows release
    ///     their stored-file references for the recurring orphan cleanup.
    /// </summary>
    Task<int> DeleteSupersededVersionsBeyondMostRecentAsync(
        int datSourceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Deletes abandoned PendingActivation versions imported before the cutoff that no
    ///     non-terminal replace job references via its persisted NewDatId. The version row
    ///     and its parsed graph go together (child rows cascade at the database); the source
    ///     anchor and its Active version are untouched. Idempotent; deleted rows release
    ///     their stored-file references for the recurring orphan cleanup.
    /// </summary>
    Task<int> DeleteAbandonedPendingVersionsAsync(
        DateTimeOffset importedBefore,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets every routed platform on a PendingActivation version older than the cutoff.
    ///     This deliberate superset ignores job phase and claim presence, so a concurrent change
    ///     that makes a candidate deletable cannot escape catalog invalidation.
    /// </summary>
    Task<IReadOnlyList<int>> GetRoutedPlatformIdsWithAgedPendingVersionsAsync(
        DateTimeOffset importedBefore,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets ids of sources holding more Superseded versions than the N=1 retention
    ///     policy allows, for the convergence sweep's retention backstop.
    /// </summary>
    Task<IReadOnlyList<int>> GetSourceIdsExceedingSupersededRetentionAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns whether one source currently exceeds the N=1 superseded-version retention
    ///     limit. Used as a read-only preflight so the immediate replacement path does not open
    ///     a no-op write transaction for the first retained superseded version.
    /// </summary>
    Task<bool> HasSupersededVersionsBeyondRetentionAsync(
        int datSourceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets every routed platform represented by any version of the source. This deliberate
    ///     superset remains safe if lifecycle/winner state changes before the retention delete
    ///     under ReadCommitted isolation.
    /// </summary>
    Task<IReadOnlyList<int>> GetRoutedPlatformIdsBySourceIdAsync(
        int datSourceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Deletes the source anchor when no DAT versions reference it anymore. Executes within
    ///     the caller's ambient transaction so a last-version delete leaves no orphan anchor.
    /// </summary>
    Task DeleteSourceIfOrphanedAsync(int datSourceId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets the neutral catalog source realized by a DAT source anchor, for handing
    ///     claims to the title derivation service.
    /// </summary>
    Task<int> GetCatalogSourceIdAsync(int datSourceId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Persists a batch of games stitched to their catalog-owned source entries. Entry
    ///     identity comes from ITitleDerivationService assignments; this method never creates
    ///     entries.
    /// </summary>
    Task<IReadOnlyList<int>> AddGamesBatchAsync(IReadOnlyList<DatGameWithEntry> games, CancellationToken cancellationToken = default);
    Task AddGameRegionsBatchAsync(IReadOnlyList<(int GameId, int RegionId)> mappings, CancellationToken cancellationToken = default);
    Task AddGameLanguagesBatchAsync(IReadOnlyList<(int GameId, int LanguageId)> mappings, CancellationToken cancellationToken = default);

    Task UpdateCountsAsync(int datFileId, int gameCount, int romCount, int diskCount,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Acquires the catalog-topology fence for a DAT mutation inside the caller-owned
    ///     transaction. Every topology writer (DAT delete/replace, title moves, derivation, and
    ///     source-link writers) takes the same fence, so read-before-delete state derived after
    ///     this call is serialized with those writers and remains the exact state consumed by the
    ///     mutation. The fence is released when the transaction ends.
    /// </summary>
    Task AcquireMutationWriteLockAsync(int datFileId, CancellationToken cancellationToken = default);

    Task DeleteAsync(int id, CancellationToken cancellationToken = default);

    Task<DatRom?> FindRomBySha1Async(Sha1 sha1, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DatRom>> FindRomsBySha1Async(Sha1 sha1, CancellationToken cancellationToken = default);

    Task<PagedList<DatGame>> GetGamesByDatIdAsync(
        int datId,
        string? cursor,
        int limit = 50,
        BiosFilter biosFilter = BiosFilter.Exclude,
        int? libraryId = null,
        CancellationToken cancellationToken = default);

    Task<PagedList<DatRom>> GetRomsByGameIdAsync(
        int gameId,
        string? cursor,
        int limit = 50,
        CancellationToken cancellationToken = default);

    Task<PagedList<DatRom>> GetRomsByDatIdAsync(
        int datId,
        string? cursor,
        int limit = 50,
        CancellationToken cancellationToken = default);

    Task<DatGame?> GetGameByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<int> LinkDatRomsToRomFileAsync(Sha1 sha1, int romFileId, CancellationToken cancellationToken = default);
    Task<int> LinkDatRomsToExistingRomFilesAsync(
        IReadOnlyList<int> datGameIds,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DatGame>> GetAllGamesByDatIdAsync(int datId, BiosFilter biosFilter = BiosFilter.Exclude,
        CancellationToken cancellationToken = default);

    Task UpdateDatFilePlatformAsync(int datFileId, int platformId, CancellationToken cancellationToken = default);
    Task<int> BackfillIsBiosAsync(CancellationToken cancellationToken = default);
}

/// <summary>A DAT game paired with the catalog source entry it realizes.</summary>
public sealed record DatGameWithEntry(DatGame Game, int SourceEntryId);
