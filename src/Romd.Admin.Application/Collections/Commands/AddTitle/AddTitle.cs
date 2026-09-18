using Romd.Application.Common.ReferenceCatalog;
using ErrorOr;
using Romd.Admin.Application.Catalog;
using Romd.Application.Common.Cqrs;
using Romd.Admin.Application.Titles;
using Romd.Contracts.Management.Collections;

namespace Romd.Admin.Application.Collections.Commands.AddTitle;

public sealed record AddTitleToCollectionCommand(
    int CollectionId,
    int TitleId,
    string? Note
) : ICommand<CollectionDetail>;

public sealed class AddTitleToCollectionCommandHandler(IReferenceCatalogService referenceCatalog,
    ICollectionRepository collectionRepository,
    ITitleRepository titleRepository
) : ICommandHandler<AddTitleToCollectionCommand, CollectionDetail>
{
    public async Task<ErrorOr<CollectionDetail>> HandleAsync(
        AddTitleToCollectionCommand command,
        CancellationToken ct = default)
    {
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
        var title = await titleRepository.GetByIdAsync(command.TitleId, ct);
        if (title is null)
            return CatalogErrors.TitleNotFound(command.TitleId);

        var collection = await collectionRepository.GetWithItemsAsync(command.CollectionId, ct);
        if (collection is null)
            return CollectionErrors.NotFound(command.CollectionId);

        if (!collection.AddItem(command.TitleId, command.Note))
            return CollectionErrors.DuplicateTitle(command.TitleId);

        await collectionRepository.UpdateAsync(collection, ct);

        var itemDetails = await collectionRepository.GetItemDetailsAsync(command.CollectionId, ct);
        return collection.ToDetail(itemDetails.ToItemDtos(systemKeys), systemKeys);
    }
}
