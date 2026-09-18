using Romd.Application.Common.ReferenceCatalog;
using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Collections;

namespace Romd.Admin.Application.Collections.Queries.GetCollectionDetail;

public sealed record GetCollectionDetailQuery(int CollectionId) : IQuery<CollectionDetail>;

public sealed class GetCollectionDetailQueryHandler(IReferenceCatalogService referenceCatalog,
    ICollectionRepository collectionRepository
) : IQueryHandler<GetCollectionDetailQuery, CollectionDetail>
{
    public async Task<ErrorOr<CollectionDetail>> HandleAsync(
        GetCollectionDetailQuery query,
        CancellationToken ct = default)
    {
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
        var collection = await collectionRepository.GetWithItemsAsync(query.CollectionId, ct);
        if (collection is null)
            return CollectionErrors.NotFound(query.CollectionId);

        var itemDetails = await collectionRepository.GetItemDetailsAsync(query.CollectionId, ct);
        return collection.ToDetail(itemDetails.ToItemDtos(systemKeys), systemKeys);
    }
}
