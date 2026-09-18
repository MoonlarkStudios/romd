using Romd.Application.Common.ReferenceCatalog;
using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Collections;

namespace Romd.Admin.Application.Collections.Commands.RemoveTitle;

public sealed record RemoveTitleFromCollectionCommand(
    int CollectionId,
    int TitleId
) : ICommand<CollectionDetail>;

public sealed class RemoveTitleFromCollectionCommandHandler(IReferenceCatalogService referenceCatalog,
    ICollectionRepository collectionRepository
) : ICommandHandler<RemoveTitleFromCollectionCommand, CollectionDetail>
{
    public async Task<ErrorOr<CollectionDetail>> HandleAsync(
        RemoveTitleFromCollectionCommand command,
        CancellationToken ct = default)
    {
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
        var collection = await collectionRepository.GetWithItemsAsync(command.CollectionId, ct);
        if (collection is null)
            return CollectionErrors.NotFound(command.CollectionId);

        if (!collection.RemoveItem(command.TitleId))
            return CollectionErrors.TitleNotInCollection(command.TitleId);

        await collectionRepository.UpdateAsync(collection, ct);

        var itemDetails = await collectionRepository.GetItemDetailsAsync(command.CollectionId, ct);
        return collection.ToDetail(itemDetails.ToItemDtos(systemKeys), systemKeys);
    }
}
