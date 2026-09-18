using Romd.Application.Common.ReferenceCatalog;
using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Collections;

namespace Romd.Admin.Application.Collections.Queries.ListCollections;

public sealed record ListCollectionsQuery(int? PlatformId = null)
    : IQuery<IReadOnlyList<CollectionSummary>>;

public sealed class ListCollectionsQueryHandler(IReferenceCatalogService referenceCatalog,
    ICollectionRepository collectionRepository
) : IQueryHandler<ListCollectionsQuery, IReadOnlyList<CollectionSummary>>
{
    public async Task<ErrorOr<IReadOnlyList<CollectionSummary>>> HandleAsync(
        ListCollectionsQuery query,
        CancellationToken ct = default)
    {
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
        var readModels = await collectionRepository.GetAllAsync(query.PlatformId, ct);
        return readModels.Select(m => m.ToSummary(systemKeys)).ToList();
    }
}
