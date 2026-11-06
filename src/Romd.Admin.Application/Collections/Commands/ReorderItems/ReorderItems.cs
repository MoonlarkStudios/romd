using Romd.Application.Common.ReferenceCatalog;
using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Collections;

namespace Romd.Admin.Application.Collections.Commands.ReorderItems;

public sealed record ReorderCollectionItemsCommand(
    int CollectionId,
    IReadOnlyList<int> TitleIds
) : ICommand<CollectionDetail>;

public sealed class ReorderCollectionItemsCommandHandler(IReferenceCatalogService referenceCatalog,
    ICollectionRepository collectionRepository,
    IUnitOfWork unitOfWork,
    ILogger<ReorderCollectionItemsCommandHandler> logger
) : ICommandHandler<ReorderCollectionItemsCommand, CollectionDetail>
{
    public async Task<ErrorOr<CollectionDetail>> HandleAsync(
        ReorderCollectionItemsCommand command,
        CancellationToken ct = default)
    {
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
        return await unitOfWork.ExecuteInTransactionAsync<CollectionDetail>(async token =>
        {
            var collection = await collectionRepository.LockForItemMutationStagedAsync(command.CollectionId, token);
            if (collection is null)
                return CollectionErrors.NotFound();

            // Checked against the current locked membership: an ordering built from a stale read is
            // rejected rather than merged, so the client reloads and orders what it can see.
            if (!collection.ReorderItems(command.TitleIds))
                return CollectionErrors.ReorderMismatch();

            await collectionRepository.ApplyItemOrderStagedAsync(command.CollectionId, collection.Items, token);
            await unitOfWork.FlushAsync(token);

            var itemDetails = await collectionRepository.GetItemDetailsAsync(command.CollectionId, token);
            return collection.ToDetail(itemDetails.ToItemDtos(systemKeys), systemKeys);
        }, logger, ct);
    }
}
