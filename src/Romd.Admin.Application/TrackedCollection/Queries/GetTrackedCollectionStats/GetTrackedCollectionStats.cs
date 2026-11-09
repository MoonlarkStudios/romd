using Romd.Application.Common.ReferenceCatalog;
using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.TrackedCollection;

namespace Romd.Admin.Application.TrackedCollection.Queries.GetTrackedCollectionStats;

public sealed record GetTrackedCollectionStatsQuery : IQuery<TrackedCollectionStats>;

public sealed class GetTrackedCollectionStatsQueryHandler(IReferenceCatalogService referenceCatalog, ITrackedCollectionReadRepository repository)
    : IQueryHandler<GetTrackedCollectionStatsQuery, TrackedCollectionStats>
{
    public async Task<ErrorOr<TrackedCollectionStats>> HandleAsync(
        GetTrackedCollectionStatsQuery query,
        CancellationToken ct = default)
    {
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
        return
        TrackedCollectionContractMapper.ToContract(await repository.GetStatsAsync(ct), systemKeys);
    }
}
