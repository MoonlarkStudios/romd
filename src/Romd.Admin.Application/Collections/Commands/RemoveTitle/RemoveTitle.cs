using Romd.Application.Common.ReferenceCatalog;
using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Collections;

namespace Romd.Admin.Application.Collections.Commands.RemoveTitle;

public sealed record RemoveTitleFromCollectionCommand(
    int CollectionId,
    int TitleId
) : ICommand<CollectionDetail>;

public sealed class RemoveTitleFromCollectionCommandHandler(IReferenceCatalogService referenceCatalog,
    ICollectionRepository collectionRepository,
    IUnitOfWork unitOfWork,
    ILogger<RemoveTitleFromCollectionCommandHandler> logger
) : ICommandHandler<RemoveTitleFromCollectionCommand, CollectionDetail>
{
    public async Task<ErrorOr<CollectionDetail>> HandleAsync(
        RemoveTitleFromCollectionCommand command,
        CancellationToken ct = default)
    {
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
        return await unitOfWork.ExecuteInTransactionAsync<CollectionDetail>(async token =>
        {
            var collection = await collectionRepository.LockForItemMutationStagedAsync(command.CollectionId, token);
            if (collection is null)
                return CollectionErrors.NotFound();

            if (!collection.RemoveItem(command.TitleId))
                return CollectionErrors.TitleNotInCollection();

            await collectionRepository.RemoveItemStagedAsync(command.CollectionId, command.TitleId, token);
            // RemoveItem compacted the order of the remaining, currently locked membership.
            await collectionRepository.ApplyItemOrderStagedAsync(command.CollectionId, collection.Items, token);
            await unitOfWork.FlushAsync(token);

            var itemDetails = await collectionRepository.GetItemDetailsAsync(command.CollectionId, token);
            return collection.ToDetail(itemDetails.ToItemDtos(systemKeys), systemKeys);
        }, logger, ct);
    }
}
