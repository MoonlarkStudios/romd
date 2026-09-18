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
        Domain.Libraries.Library? library = null;
        bool commitSucceeded = false;
        try
        {
            await using var transaction = await unitOfWork.BeginTransactionAsync(ct);

            library = await libraryRepository.GetByIdAsync(command.LibraryId, ct);
            if (library is null)
            {
                return LibraryErrors.NotFound();
            }

            if (library.IsDefault)
            {
                return LibraryErrors.DefaultLibraryUndeletable();
            }

            if (!await libraryRepository.DeleteAsync(library.Id, ct))
            {
                return LibraryErrors.NotFound();
            }

            await outbox.EnqueueAsync(
                AdminRealtimeEventTypes.LibraryUpdated,
                library.ToUpdatedPayload(),
                ct);
            await transaction.CommitAsync(ct);
            commitSucceeded = true;
        }
        catch (Exception ex) when (commitSucceeded)
        {
            logger.LogWarning(
                ex,
                "Library deletion committed but transaction cleanup failed (Library ID: {LibraryId})",
                library?.Id);
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

        return Result.Deleted;
    }
}
