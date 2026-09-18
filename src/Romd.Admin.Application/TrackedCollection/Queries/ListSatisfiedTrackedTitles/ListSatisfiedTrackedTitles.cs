using Romd.Application.Common.ReferenceCatalog;
using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.TrackedCollection;

namespace Romd.Admin.Application.TrackedCollection.Queries.ListSatisfiedTrackedTitles;

public sealed record ListSatisfiedTrackedTitlesQuery : IQuery<IReadOnlyList<TrackedCollectionTitleDto>>;

public sealed class ListSatisfiedTrackedTitlesQueryHandler(IReferenceCatalogService referenceCatalog, ITrackedCollectionReadRepository repository)
    : IQueryHandler<ListSatisfiedTrackedTitlesQuery, IReadOnlyList<TrackedCollectionTitleDto>>
{
    public async Task<ErrorOr<IReadOnlyList<TrackedCollectionTitleDto>>> HandleAsync(
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
