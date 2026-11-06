using Romd.Application.Common.ReferenceCatalog;
using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Collections;

namespace Romd.Admin.Application.Collections.Commands.UpdateItemNote;

public sealed record UpdateCollectionItemNoteCommand(
    int CollectionId,
    int TitleId,
    string? Note
) : ICommand<CollectionDetail>;

public sealed class UpdateCollectionItemNoteCommandHandler(IReferenceCatalogService referenceCatalog,
    ICollectionRepository collectionRepository,
    IUnitOfWork unitOfWork,
    ILogger<UpdateCollectionItemNoteCommandHandler> logger
) : ICommandHandler<UpdateCollectionItemNoteCommand, CollectionDetail>
{
    public async Task<ErrorOr<CollectionDetail>> HandleAsync(
        UpdateCollectionItemNoteCommand command,
        CancellationToken ct = default)
    {
        if (command.Note is { Length: > Domain.Collections.CollectionItem.MaxNoteLength })
            return CollectionErrors.NoteTooLong();

        var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
        return await unitOfWork.ExecuteInTransactionAsync<CollectionDetail>(async token =>
        {
            var collection = await collectionRepository.LockForItemMutationStagedAsync(command.CollectionId, token);
            if (collection is null)
                return CollectionErrors.NotFound();

            var item = collection.Items.FirstOrDefault(i => i.TitleId == command.TitleId);
            if (item is null)
                return CollectionErrors.TitleNotInCollection();

            item.UpdateNote(command.Note);
            await collectionRepository.UpdateItemNoteStagedAsync(item, token);
            await unitOfWork.FlushAsync(token);

            var itemDetails = await collectionRepository.GetItemDetailsAsync(command.CollectionId, token);
            return collection.ToDetail(itemDetails.ToItemDtos(systemKeys), systemKeys);
        }, logger, ct);
    }
}
