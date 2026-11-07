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
        var collection = await collectionRepository.GetByIdAsync(query.CollectionId, ct);
        if (collection is null)
            return CollectionErrors.NotFound();

        var page = await collectionRepository.GetItemPageAsync(query.CollectionId, null, 50, ct);
        if (page.IsError)
            return page.Errors;
        var detail = collection.ToDetail(page.Value.Items.ToItemDtos(systemKeys), systemKeys);
        return detail with
        {
            ItemCount = await collectionRepository.CountItemsAsync(query.CollectionId, ct),
            Items = new Romd.Contracts.Common.Models.Page<CollectionItem>
            { Items = page.Value.Items.ToItemDtos(systemKeys), NextCursor = page.Value.NextCursor, HasNextPage = page.Value.HasNextPage }
        };
    }
}
