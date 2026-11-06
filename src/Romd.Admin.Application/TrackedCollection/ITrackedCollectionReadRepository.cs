using ErrorOr;
using Romd.Application.Common.Pagination;
using Romd.Admin.Application.TrackedCollection.ReadModels;

namespace Romd.Admin.Application.TrackedCollection;

public interface ITrackedCollectionReadRepository
{
    Task<ErrorOr<PagedList<TrackedCollectionTitleData>>> ListPageAsync(TrackedCollectionView view, string? query, string? cursor, int limit, CancellationToken ct);

    Task<IReadOnlyList<TrackedCollectionTitleData>> ListAsync(
        TrackedCollectionView view,
        CancellationToken cancellationToken = default);

    Task<TrackedCollectionStatsData> GetStatsAsync(CancellationToken cancellationToken = default);
}

public enum TrackedCollectionView
{
    Satisfied,
    Missing,
    Upgrades
}
