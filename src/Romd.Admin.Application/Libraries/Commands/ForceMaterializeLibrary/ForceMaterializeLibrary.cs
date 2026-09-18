using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Common.Realtime;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Domain.Jobs;
using Romd.Domain.Libraries;

namespace Romd.Admin.Application.Libraries.Commands.ForceMaterializeLibrary;

public sealed record ForceMaterializeLibraryCommand(int LibraryId) : ICommand<Guid>;

public sealed class ForceMaterializeLibraryCommandHandler(
    ILibraryRepository libraryRepository,
    IMaterializationJobRepository materializationJobs,
    IMaterializationJobEnqueuer materializationJobEnqueuer,
    IAdminEventOutbox outbox,
    ICurrentUser currentUser,
    IUnitOfWork unitOfWork,
    ILogger<ForceMaterializeLibraryCommandHandler> logger)
    : ICommandHandler<ForceMaterializeLibraryCommand, Guid>
{
    public async Task<ErrorOr<Guid>> HandleAsync(
        ForceMaterializeLibraryCommand command,
        CancellationToken ct = default)
    {
        MaterializationJob? acceptedJob = null;
        bool createdJob = false;
        bool accelerateExistingJob = false;
        bool commitSucceeded = false;
        try
        {
            await using var transaction = await unitOfWork.BeginTransactionAsync(ct);

            Library? library = await libraryRepository.GetByIdAsync(command.LibraryId, ct);
            if (library is null)
            {
                return LibraryErrors.NotFound();
            }

            acceptedJob = await materializationJobs.GetActiveForLibraryAsync(library.Id, ct);
            if (acceptedJob is not null)
            {
                accelerateExistingJob = acceptedJob.PhaseEnum == MaterializationPhase.Pending
                    && string.IsNullOrWhiteSpace(acceptedJob.HangfireJobId);
            }
            else
            {
                library = await libraryRepository.FlagForRematerializationAsync(library.Id, ct);
                if (library is null)
                    return LibraryErrors.NotFound();

                acceptedJob = MaterializationJob.Create(library.Id, library.Name, currentUser.UserId);

                await materializationJobs.AddStagedAsync(acceptedJob, ct);
                await outbox.EnqueueAsync(
                    AdminRealtimeEventTypes.LibraryUpdated,
                    library.ToUpdatedPayload(),
                    ct);
                await transaction.CommitAsync(ct);
                commitSucceeded = true;
                createdJob = true;
            }
        }
        catch (Exception ex) when (commitSucceeded)
        {
            logger.LogWarning(
                ex,
                "Library materialization request committed but transaction cleanup failed " +
                "(Library ID: {LibraryId}, Job ID: {JobId})",
                command.LibraryId,
                acceptedJob?.Id);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            MaterializationJob? convergedJob = await TryGetActiveJobAsync(command.LibraryId, ct);
            if (convergedJob is not null)
            {
                logger.LogWarning(
                    ex,
                    "Library materialization request lost a concurrent accept race but converged " +
                    "on active job {JobId} (Library ID: {LibraryId})",
                    convergedJob.Id,
                    command.LibraryId);
                return convergedJob.Id;
            }

            logger.LogError(
                ex,
                "Failed to accept library materialization request (Library ID: {LibraryId})",
                command.LibraryId);
            return LibraryErrors.PersistenceFailed();
        }

        if (createdJob || accelerateExistingJob)
        {
            await TryEnqueueAsync(acceptedJob!.Id, ct);
        }

        return acceptedJob!.Id;
    }

    private async Task<MaterializationJob?> TryGetActiveJobAsync(int libraryId, CancellationToken ct)
    {
        try
        {
            return await materializationJobs.GetActiveForLibraryAsync(libraryId, ct);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task TryEnqueueAsync(Guid jobId, CancellationToken ct)
    {
        try
        {
            await materializationJobEnqueuer.EnqueueAsync(jobId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Library materialization request {JobId} committed but enqueue acceleration failed; " +
                "the worker dispatcher will recover",
                jobId);
        }
    }
}
