using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Titles;

public interface ITrackedTitleRepository
{
    Task<TrackedTitle?> GetAsync(int titleId, CancellationToken cancellationToken = default);

    Task<TrackTitleResult> TrackAsync(
        int titleId,
        int? pinnedCatalogReleaseId = null,
        CancellationToken cancellationToken = default);

    Task<bool> UntrackAsync(int titleId, CancellationToken cancellationToken = default);

    Task<int> TrackByTitleIdsAsync(
        IReadOnlyCollection<int> titleIds,
        CancellationToken cancellationToken = default);

    Task<int> UntrackByTitleIdsAsync(
        IReadOnlyCollection<int> titleIds,
        CancellationToken cancellationToken = default);

    Task<int> TrackByPlatformAsync(int platformId, CancellationToken cancellationToken = default);
    Task<int> UntrackByPlatformAsync(int platformId, CancellationToken cancellationToken = default);
    Task<int> TrackByDatSourceAsync(int datSourceId, CancellationToken cancellationToken = default);
    Task<int> UntrackByDatSourceAsync(int datSourceId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Consolidates source tracking into a merge target. A target pin is retained; a source pin
    ///     cannot cross title boundaries and is cleared while preserving the tracked intent.
    /// </summary>
    Task ConsolidateAsync(int sourceTitleId, int targetTitleId, CancellationToken cancellationToken = default);
}

public enum TrackTitleResult
{
    Updated,
    TitleNotFound,
    PinnedCatalogReleaseNotFound,
    PinnedCatalogReleaseTitleMismatch
}
