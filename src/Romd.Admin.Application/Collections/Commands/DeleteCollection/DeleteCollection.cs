using ErrorOr;
using Romd.Application.Common.Cqrs;

namespace Romd.Admin.Application.Collections.Commands.DeleteCollection;

public sealed record DeleteCollectionCommand(int Id) : ICommand;

public sealed class DeleteCollectionCommandHandler(
    ICollectionRepository collectionRepository
) : ICommandHandler<DeleteCollectionCommand, Deleted>
{
    public async Task<ErrorOr<Deleted>> HandleAsync(
        DeleteCollectionCommand command,
        CancellationToken ct = default)
    {
        var collection = await collectionRepository.GetByIdAsync(command.Id, ct);
        if (collection is null)
            return CollectionErrors.NotFound(command.Id);

        await collectionRepository.DeleteAsync(command.Id, ct);

        return Result.Deleted;
    }
}
