using Romd.Application.Common.ReferenceCatalog;
using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Collections;

namespace Romd.Admin.Application.Collections.Commands.ReorderItems;

public sealed record ReorderCollectionItemsCommand(
    int CollectionId,
    IReadOnlyList<int> TitleIds
) : ICommand<CollectionDetail>;

public sealed class ReorderCollectionItemsCommandHandler(IReferenceCatalogService referenceCatalog,
    ICollectionRepository collectionRepository
) : ICommandHandler<ReorderCollectionItemsCommand, CollectionDetail>
{
    public async Task<ErrorOr<CollectionDetail>> HandleAsync(
        ReorderCollectionItemsCommand command,
        CancellationToken ct = default)
    {
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
        var collection = await collectionRepository.GetWithItemsAsync(command.CollectionId, ct);
        if (collection is null)
            return CollectionErrors.NotFound(command.CollectionId);

        if (!collection.ReorderItems(command.TitleIds))
            return CollectionErrors.ReorderMismatch();

        await collectionRepository.UpdateAsync(collection, ct);

        var itemDetails = await collectionRepository.GetItemDetailsAsync(command.CollectionId, ct);
        return collection.ToDetail(itemDetails.ToItemDtos(systemKeys), systemKeys);
    }
}
