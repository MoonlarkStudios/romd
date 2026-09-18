using Romd.Application.Common.ReferenceCatalog;
using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Cqrs;
using Romd.Contracts.Management.Collections;

namespace Romd.Admin.Application.Collections.Commands.UpdateCollection;

public sealed record UpdateCollectionCommand(
    int Id,
    string Name,
    string? Description,
    int? CoverMediaId,
    int? PlatformId
) : ICommand<CollectionSummary>;

public sealed class UpdateCollectionCommandHandler(IReferenceCatalogService referenceCatalog,
    ICollectionRepository collectionRepository,
    IUnitOfWork unitOfWork,
    ILogger<UpdateCollectionCommandHandler> logger
) : ICommandHandler<UpdateCollectionCommand, CollectionSummary>
{
    public async Task<ErrorOr<CollectionSummary>> HandleAsync(
        UpdateCollectionCommand command,
        CancellationToken ct = default)
    {
        var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
        if (string.IsNullOrWhiteSpace(command.Name))
            return CollectionErrors.EmptyName();

        return await unitOfWork.ExecuteInTransactionAsync<CollectionSummary>(async token =>
        {
            var collection = await collectionRepository.GetByIdAsync(command.Id, token);
            if (collection is null)
                return CollectionErrors.NotFound(command.Id);

            collection.UpdateDetails(command.Name, command.Description, command.CoverMediaId, command.PlatformId);
            var summary = await collectionRepository.UpdateDetailsStagedAsync(collection, token);
            if (summary is null)
                return CollectionErrors.NotFound(command.Id);

            return summary.ToSummary(systemKeys);
        }, logger, ct);
    }
}
