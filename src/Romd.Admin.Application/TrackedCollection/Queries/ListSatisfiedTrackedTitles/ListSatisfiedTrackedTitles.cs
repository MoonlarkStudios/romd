using Romd.Application.Common.ReferenceCatalog;
using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.TrackedCollection;

namespace Romd.Admin.Application.TrackedCollection.Queries.ListSatisfiedTrackedTitles;

public sealed record ListSatisfiedTrackedTitlesQuery : IQuery<IReadOnlyList<TrackedCollectionTitle>>;

public sealed class ListSatisfiedTrackedTitlesQueryHandler(IReferenceCatalogService referenceCatalog, ITrackedCollectionReadRepository repository)
    : IQueryHandler<ListSatisfiedTrackedTitlesQuery, IReadOnlyList<TrackedCollectionTitle>>
{
    public async Task<ErrorOr<IReadOnlyList<TrackedCollectionTitle>>> HandleAsync(
        ListSatisfiedTrackedTitlesQuery query,
        CancellationToken ct = default)
    {
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
        return
        (await repository.ListAsync(TrackedCollectionView.Satisfied, ct))
        .Select(item => TrackedCollectionContractMapper.ToContract(item, systemKeys))
        .ToList();
    }
}
