using Romd.Application.Common.ReferenceCatalog;
using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.TrackedCollection;

namespace Romd.Admin.Application.TrackedCollection.Queries.ListMissingTrackedTitles;

public sealed record ListMissingTrackedTitlesQuery : IQuery<IReadOnlyList<TrackedCollectionTitle>>;

public sealed class ListMissingTrackedTitlesQueryHandler(IReferenceCatalogService referenceCatalog, ITrackedCollectionReadRepository repository)
    : IQueryHandler<ListMissingTrackedTitlesQuery, IReadOnlyList<TrackedCollectionTitle>>
{
    public async Task<ErrorOr<IReadOnlyList<TrackedCollectionTitle>>> HandleAsync(
        ListMissingTrackedTitlesQuery query,
        CancellationToken ct = default)
    {
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
        return
        (await repository.ListAsync(TrackedCollectionView.Missing, ct))
        .Select(item => TrackedCollectionContractMapper.ToContract(item, systemKeys))
        .ToList();
    }
}
