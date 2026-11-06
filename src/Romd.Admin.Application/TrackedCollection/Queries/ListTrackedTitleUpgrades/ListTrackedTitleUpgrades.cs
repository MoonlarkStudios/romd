using Romd.Application.Common.ReferenceCatalog;
using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.TrackedCollection;

namespace Romd.Admin.Application.TrackedCollection.Queries.ListTrackedTitleUpgrades;

public sealed record ListTrackedTitleUpgradesQuery : IQuery<IReadOnlyList<TrackedCollectionTitleDto>>;

public sealed class ListTrackedTitleUpgradesQueryHandler(IReferenceCatalogService referenceCatalog, ITrackedCollectionReadRepository repository)
    : IQueryHandler<ListTrackedTitleUpgradesQuery, IReadOnlyList<TrackedCollectionTitleDto>>
{
    public async Task<ErrorOr<IReadOnlyList<TrackedCollectionTitleDto>>> HandleAsync(
        ListTrackedTitleUpgradesQuery query,
        CancellationToken ct = default)
    {
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
        return
        (await repository.ListAsync(TrackedCollectionView.Upgrades, ct))
        .Select(item => TrackedCollectionContractMapper.ToContract(item, systemKeys))
        .ToList();
    }
}
