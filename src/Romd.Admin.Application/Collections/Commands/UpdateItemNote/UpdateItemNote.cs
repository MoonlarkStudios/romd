using Romd.Application.Common.ReferenceCatalog;
using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Collections;

namespace Romd.Admin.Application.Collections.Commands.UpdateItemNote;

public sealed record UpdateCollectionItemNoteCommand(
    int CollectionId,
    int TitleId,
    string? Note
) : ICommand<CollectionDetail>;

public sealed class UpdateCollectionItemNoteCommandHandler(IReferenceCatalogService referenceCatalog,
    ICollectionRepository collectionRepository
) : ICommandHandler<UpdateCollectionItemNoteCommand, CollectionDetail>
{
    public async Task<ErrorOr<CollectionDetail>> HandleAsync(
        UpdateCollectionItemNoteCommand command,
        CancellationToken ct = default)
    {
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
        var collection = await collectionRepository.GetWithItemsAsync(command.CollectionId, ct);
        if (collection is null)
            return CollectionErrors.NotFound(command.CollectionId);

        var item = collection.Items.FirstOrDefault(i => i.TitleId == command.TitleId);
        if (item is null)
            return CollectionErrors.TitleNotInCollection(command.TitleId);

        item.UpdateNote(command.Note);
        await collectionRepository.UpdateAsync(collection, ct);

        var itemDetails = await collectionRepository.GetItemDetailsAsync(command.CollectionId, ct);
        return collection.ToDetail(itemDetails.ToItemDtos(systemKeys), systemKeys);
    }
}
