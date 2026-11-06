using Romd.Application.Common.ReferenceCatalog;
using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
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
    ICollectionRepository collectionRepository,
    ICollectionCoverMediaReader covers,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<CreateCollectionCommandHandler> logger
) : ICommandHandler<CreateCollectionCommand, CollectionSummary>
{
    public async Task<ErrorOr<CollectionSummary>> HandleAsync(
        CreateCollectionCommand command,
        CancellationToken ct = default)
    {
        if (CollectionInputValidation.DetailsError(command.Name, command.Description) is { } invalid)
            return invalid;

        var systemKeys = await referenceCatalog.GetSystemKeysAsync(ct);
        var collection = Domain.Collections.Collection.CreateNew(
            command.Name,
            clock.GetUtcNow(),
            command.Description,
            command.CoverMediaId,
            command.PlatformId);

        return await unitOfWork.ExecuteInTransactionAsync<CollectionSummary>(async token =>
        {
            if (command.CoverMediaId is { } mediaId && !await covers.LockStagedAsync(mediaId, token))
                return CollectionErrors.CoverMediaNotFound();

            var creation = await collectionRepository.AddStagedAsync(collection, token);
            await unitOfWork.FlushAsync(token);
            return creation.ReadAfterFlush().ToSummary(0, systemKeys);
        }, logger, ct);
    }
}
