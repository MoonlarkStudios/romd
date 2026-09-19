using Romd.Admin.Application.Titles.ReadModels;
using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Titles;

/// <summary>
///     Port for Title data access.
/// </summary>
public interface ITitleRepository
{
    Task AppendGalleryMediaStagedAsync(TitleMedia media, CancellationToken cancellationToken = default);

    Task AddUserMediaStagedAsync(TitleMedia media, CancellationToken cancellationToken = default);
    /// <summary>
    ///     Gets a title by ID.
    /// </summary>
    Task<Title?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets a title by platform and normalized name.
    /// </summary>
    Task<Title?> GetByNormalizedNameAsync(int platformId, string normalizedName, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets all titles for a platform, filtered by the specified library.
    /// </summary>
    /// <param name="platformId">The platform ID to filter by.</param>
    /// <param name="libraryId">
    ///     The library ID to apply filtering, or null for unrestricted access.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Filtered titles ordered by name.</returns>
    Task<IReadOnlyList<Title>> GetByPlatformFilteredAsync(
        int platformId,
        int? libraryId,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets titles that have local ROM files (owned titles) for a platform.
    /// </summary>
    Task<IReadOnlyList<Title>> GetWithLocalPayloadByPlatformAsync(
        int platformId,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets owned titles for a platform, filtered by the specified library.
    /// </summary>
    /// <param name="platformId">The platform ID to filter by.</param>
    /// <param name="libraryId">
    ///     The library ID to apply filtering, or null for unrestricted access.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Filtered owned titles ordered by name.</returns>
    Task<IReadOnlyList<Title>> GetWithLocalPayloadByPlatformFilteredAsync(
        int platformId,
        int? libraryId,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets existing titles by normalized names for batch matching.
    ///     Returns a dictionary mapping normalized name to Title for efficient lookup.
    /// </summary>
    Task<Dictionary<string, Title>> GetByNormalizedNamesAsync(
        int platformId,
        IEnumerable<string> normalizedNames,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets titles pending enrichment.
    /// </summary>
    Task<IReadOnlyList<Title>> GetPendingEnrichmentAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets a title by external provider ID.
    ///     Includes ExternalIds and Media collections.
    /// </summary>
    Task<Title?> GetByExternalIdAsync(string provider, string externalId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets a title by ID with related collections loaded.
    ///     Includes ExternalIds and Media for enrichment operations.
    /// </summary>
    Task<Title?> GetWithCollectionsAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets a title by ID with all collections including metadata layers.
    ///     Use for metadata update operations that need layer data.
    /// </summary>
    Task<Title?> GetWithMetadataLayersAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets full title detail including linked DatGames, DatRoms, and RomFile status.
    ///     Returns the Trinity view: Title -> Releases (DatGames) -> Files (DatRoms with ownership).
    /// </summary>
    Task<TitleDetailData?> GetTitleDetailAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Adds a new title.
    /// </summary>
    Task<Title> AddAsync(Title title, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Adds multiple titles in a batch and returns them with database-assigned IDs.
    /// </summary>
    Task<IReadOnlyList<Title>> AddRangeAsync(IReadOnlyList<Title> titles, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Updates a title (for enrichment updates).
    /// </summary>
    Task UpdateAsync(Title title, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Stages a title update without saving. The caller owns the flush and commit.
    /// </summary>
    Task UpdateStagedAsync(Title title, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Stages rematerialized metadata, provenance, field overrides, primary-media selection,
    ///     and authoritative content ratings without replacing persistence-only title state or
    ///     owning the flush.
    /// </summary>
    Task UpdateMaterializedMetadataStagedAsync(
        Title title,
        CancellationToken cancellationToken = default);

    /// <summary>Stages the user metadata layer and rematerialized state in the caller-owned transaction.</summary>
    Task UpdateUserMetadataStagedAsync(Title title, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns title IDs whose normalized projection says any effective source assertion
    ///     has local payload. Provider payload topology is not consulted by this read.
    /// </summary>
    Task<IReadOnlySet<int>> GetTitleIdsWithLocalPayloadAsync(
        IEnumerable<int> titleIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns the title's provider-neutral effective-source local-payload fact.
    /// </summary>
    Task<bool> HasLocalPayloadAsync(int titleId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets a title with all collections for merge operations.
    ///     Includes ExternalIds, Media, and MetadataLayers.
    /// </summary>
    Task<Title?> GetForMergeAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves retained artwork and consolidates role selections inside a caller-owned transaction.
    /// The caller owns the flush and commit; source pending imports are invalidated.
    /// </summary>
    Task StageArtworkMergeAsync(int sourceTitleId, int targetTitleId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Deletes a title by ID unless tracked intent requires retaining it as UserOnly.
    /// </summary>
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets ids of titles with no source backing (zero truth-level links) that are not
    ///     already retained as UserOnly and were created before the cutoff — the grace window
    ///     protects titles mid-derivation between creation and linking. Backstop input for
    ///     the convergence sweep's orphan policy.
    /// </summary>
    Task<IReadOnlyList<int>> GetOrphanedTitleIdsAsync(
        DateTimeOffset createdBefore,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Removes a title that has lost all its source backing. When the title still carries
    ///     user/curated state (tracking, field overrides, confirmed external IDs, primary media,
    ///     collection membership) it is retained and marked <c>UserOnly</c> instead of deleted.
    ///     Zero links are re-checked atomically at mutation time, so a title re-linked by a
    ///     concurrent derivation after the caller's orphan check loses nothing (the call then
    ///     mutates nothing and returns <c>false</c>). Hard deletion re-points releases that a
    ///     surviving source still asserts (release identity is public and fingerprint-stable)
    ///     and deletes only the releases nobody else asserts. PRECONDITION (enforced): a
    ///     caller-owned transaction must be open — the mutations span multiple statements.
    ///     Returns <c>true</c> when the title was hard-deleted, <c>false</c> otherwise.
    /// </summary>
    Task<bool> DeleteOrRetainAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Checks if a title has any associated games.
    /// </summary>
    Task<bool> HasGamesAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Checks current tracking intent, optionally restricted to matched title IDs.</summary>
    Task<bool> HasTrackedAsync(IReadOnlyCollection<int>? titleIds = null, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Legacy ingest seam that idempotently creates tracked-title intent rows for titles that
    ///     first become owned. Manual tracking mutations use <see cref="ITrackedTitleRepository" />.
    /// </summary>
    Task MarkTrackedAsync(IReadOnlyCollection<int> titleIds, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets titles needing enrichment for a platform.
    ///     Returns titles with EnrichmentStatus of None, Pending, or LowConfidence, ordered for optimal processing.
    /// </summary>
    /// <param name="platformId">The platform to select titles for.</param>
    /// <param name="scope">
    ///     Whether to restrict selection to tracked titles (the default) or include every title.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task<IReadOnlyList<Title>> GetNeedingEnrichmentByPlatformAsync(
        int platformId,
        EnrichmentScope scope,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets title IDs for a platform. Lightweight query for batch operations.
    /// </summary>
    Task<IReadOnlyList<int>> GetIdsByPlatformAsync(
        int platformId,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets enrichment statistics aggregated from Title.EnrichmentStatus.
    /// </summary>
    Task<TitleEnrichmentStats> GetEnrichmentStatsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
///     Statistics about enrichment status across titles.
/// </summary>
public sealed record TitleEnrichmentStats(
    int Pending,
    int Completed,
    int NotFound,
    int Failed,
    int LowConfidence);
