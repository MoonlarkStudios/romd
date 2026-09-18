using Romd.Admin.Application.TrackedCollection.ReadModels;

namespace Romd.Admin.Application.TrackedCollection;

public interface ITrackedCollectionReadRepository
{
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
