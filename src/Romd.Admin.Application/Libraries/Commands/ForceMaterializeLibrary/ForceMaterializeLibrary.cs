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

public sealed record ForceMaterializeLibraryCommand(int LibraryId, Guid? ReviewedRevision = null) : ICommand<Guid>;

public sealed class ForceMaterializeLibraryCommandHandler(
    ILibraryRepository libraryRepository,
    IMaterializationJobRepository materializationJobs,
    IMaterializationJobEnqueuer materializationJobEnqueuer,
    IAdminEventOutbox outbox,
    ICurrentUser currentUser,
    IUnitOfWork unitOfWork,
    ILogger<ForceMaterializeLibraryCommandHandler> logger,
    TimeProvider clock)
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
            var transaction = await unitOfWork.BeginTransactionAsync(ct);
            bool failed = false;
            try
            {
                Library? library = await libraryRepository.GetByIdAsync(command.LibraryId, ct);
                if (library is null)
                {
                    return LibraryErrors.NotFound();
                }
                if (command.ReviewedRevision is { } revision && library.MaterializationRevision != revision)
                    return ReviewExpired();

                acceptedJob = await materializationJobs.GetActiveForLibraryAsync(library.Id, ct);
                if (acceptedJob is not null)
                {
                    if (!MatchesReview(acceptedJob, command.ReviewedRevision))
                        return Error.Conflict("Libraries.PublicationRunning", "Another publication is running. Review changes again when it finishes.");
                    accelerateExistingJob = acceptedJob.PhaseEnum == MaterializationPhase.Pending
                        && string.IsNullOrWhiteSpace(acceptedJob.HangfireJobId);
                }
                else
                {
                    if (command.ReviewedRevision is { } expected)
                    {
                        if (!await libraryRepository.FlagReviewedPublicationAsync(library.Id, expected, ct))
                            return ReviewExpired();
                        library = await libraryRepository.GetByIdAsync(library.Id, ct);
                    }
                    else
                        library = await libraryRepository.FlagForRematerializationAsync(library.Id, ct);
                    if (library is null)
                        return LibraryErrors.NotFound();

                    acceptedJob = MaterializationJob.Create(library.Id, library.Name, clock.GetUtcNow(), currentUser.UserId, command.ReviewedRevision);

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
            catch
            {
                failed = true;
                throw;
            }
            finally
            {
                await transaction.DisposePreservingOutcomeAsync(commitSucceeded, failed, logger);
            }
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
                if (!MatchesReview(convergedJob, command.ReviewedRevision))
                    return ReviewExpired();
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

    private static bool MatchesReview(MaterializationJob job, Guid? revision) =>
        revision is null || job.ReviewedRevision == revision;

    private static Error ReviewExpired() =>
        Error.Conflict("Libraries.PublicationReviewExpired", "The library changed. Review publication changes again.");

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
