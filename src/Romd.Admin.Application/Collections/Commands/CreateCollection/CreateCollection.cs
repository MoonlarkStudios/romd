using Romd.Application.Common.ReferenceCatalog;
using ErrorOr;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Collections;

namespace Romd.Admin.Application.Collections.Commands.CreateCollection;

public sealed record CreateCollectionCommand(
    string Name,
    string? Description,
    int? CoverMediaId,
    int? PlatformId
) : ICommand<CollectionSummary>;

public sealed class CreateCollectionCommandHandler(IReferenceCatalogService referenceCatalog,
    ICollectionRepository collectionRepository
) : ICommandHandler<CreateCollectionCommand, CollectionSummary>
{
    public async Task<ErrorOr<CollectionSummary>> HandleAsync(
        CreateCollectionCommand command,
        CancellationToken ct = default)
    {
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
        if (string.IsNullOrWhiteSpace(command.Name))
            return CollectionErrors.EmptyName();

        var collection = Domain.Collections.Collection.CreateNew(
            command.Name,
            command.Description,
            command.CoverMediaId,
            command.PlatformId);

        collection = await collectionRepository.AddAsync(collection, ct);

        return collection.ToSummary(0, systemKeys);
    }
}
