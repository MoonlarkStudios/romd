using Romd.Application.Common.ReferenceCatalog;
using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Titles;
using Romd.Contracts.Management.Collections;

namespace Romd.Admin.Application.Collections.Commands.AddTitle;

public sealed record AddTitleToCollectionCommand(
    int CollectionId,
    int TitleId,
    string? Note
) : ICommand<CollectionItem>;

public sealed class AddTitleToCollectionCommandHandler(IReferenceCatalogService referenceCatalog,
    ICollectionRepository collectionRepository,
    ITitleReader titleReader,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<AddTitleToCollectionCommandHandler> logger
) : ICommandHandler<AddTitleToCollectionCommand, CollectionItem>
{
    public async Task<ErrorOr<CollectionItem>> HandleAsync(
        AddTitleToCollectionCommand command,
        CancellationToken ct = default)
    {
        if (command.Note is { Length: > Domain.Collections.CollectionItem.MaxNoteLength })
            return CollectionErrors.NoteTooLong();

        var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
        var title = await titleReader.GetSummaryAsync(command.TitleId, ct);
        if (title is null)
            return CatalogErrors.TitleNotFound();

        return await unitOfWork.ExecuteInTransactionAsync<CollectionItem>(async token =>
        {
            var collection = await collectionRepository.LockForItemMutationStagedAsync(command.CollectionId, token);
            if (collection is null)
                return CollectionErrors.NotFound();

            if (collection.AddItem(command.TitleId, clock.GetUtcNow(), command.Note) is not { } item)
                return CollectionErrors.DuplicateTitle();

            await collectionRepository.AddItemStagedAsync(item, token);
            await unitOfWork.FlushAsync(token);

            // Capture the accepted representation while this transaction still owns the lock.
            var detail = await collectionRepository.GetItemDetailAsync(command.CollectionId, command.TitleId, token);
            return detail is null ? CollectionErrors.TitleNotInCollection() : detail.ToItemDto(systemKeys);
        }, logger, ct);
    }
}
