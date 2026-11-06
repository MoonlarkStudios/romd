using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Application.Common.Cqrs;

namespace Romd.Admin.Application.Libraries.Commands.DeleteLibrary;

public sealed record DeleteLibraryCommand(int LibraryId) : ICommand<Deleted>;

public sealed class DeleteLibraryCommandHandler(
    ILibraryRepository libraryRepository,
    IAdminEventOutbox outbox,
    IUnitOfWork unitOfWork,
    ILogger<DeleteLibraryCommandHandler> logger)
    : ICommandHandler<DeleteLibraryCommand, Deleted>
{
    public async Task<ErrorOr<Deleted>> HandleAsync(
        DeleteLibraryCommand command,
        CancellationToken ct = default)
    {
        try
        {
            return await unitOfWork.ExecuteInTransactionAsync<Deleted>(async token =>
            {
                var library = await libraryRepository.GetByIdAsync(command.LibraryId, token);
                if (library is null)
                {
                    return LibraryErrors.NotFound();
                }

                if (library.IsDefault)
                {
                    return LibraryErrors.DefaultLibraryUndeletable();
                }

                if (!await libraryRepository.DeleteAsync(library.Id, token))
                {
                    return LibraryErrors.NotFound();
                }

                await outbox.EnqueueAsync(
                    AdminRealtimeEventTypes.LibraryUpdated,
                    library.ToUpdatedPayload(),
                    token);
                return Result.Deleted;
            }, logger, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete library");
            return LibraryErrors.PersistenceFailed();
        }
    }
}
