using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Preparation;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Cqrs;
using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Ingestion.Import.Commands.StartServerImport;

public sealed record StartServerImportCommand(Guid? ActorId, Guid? RetryJobId = null, string? ReviewedRevision = null)
    : ICommand<ServerImportResult>;

public sealed record ServerImportResult(ServerImportView Status, Guid? AcceptedJobId = null);

public sealed class StartServerImportCommandHandler(
    IServerImportOptions options, IServerImportSource source, IServerImportStore store, IUnitOfWork work,
    IJobWorkspaceExclusion exclusion, IPathImportPreparation preparation, IUploadJobAcceptance acceptance,
    ServerImportRetryPlanner retryPlanner, TimeProvider clock, ILogger<StartServerImportCommandHandler> logger)
    : ICommandHandler<StartServerImportCommand, ServerImportResult>
{
    public async Task<ErrorOr<ServerImportResult>> HandleAsync(
        StartServerImportCommand command, CancellationToken ct = default)
    {
        if (options.ServerImportDirectory is not { } directory)
            return Error.NotFound("Import.ServerDirectoryDisabled", "A server import folder has not been configured.");

        if (command.RetryJobId == Guid.Empty ||
            (command.RetryJobId.HasValue && (command.ReviewedRevision is not { Length: 64 } revision || !revision.All(Uri.IsHexDigit))) ||
            (!command.RetryJobId.HasValue && command.ReviewedRevision is not null))
            return Error.Validation("Import.InvalidRetryReview", "Provide a job and its reviewed revision together to retry an import.");

        if (command.RetryJobId is null)
        {
            var activity = await store.ReadActivityAsync(directory, ct);
            if (await store.ReadJobAsync(activity?.JobId, ct) is { IsTerminal: false } running)
                return new ServerImportResult(ServerImportView.Create(options, clock, activity, running), running.Id);
        }

        var startedAt = clock.GetUtcNow();
        var scan = command.RetryJobId is null ? source.Scan(directory, startedAt, ct) : default;
        var jobId = Guid.NewGuid();
        ITransaction? transaction = null;
        IJobWorkspaceLease? lease = null;
        IPreparedJobWorkspace? workspace = null;
        bool commitAttempted = false;
        bool settled = false;
        Exception? primary = null;
        try
        {
            transaction = await work.BeginTransactionAsync(ct);
            await store.AcquireAcceptanceLockAsync(ct);
            var previous = await store.ReadActivityAsync(directory, ct);
            var lastJob = await store.ReadJobAsync(previous?.JobId, ct);
            if (lastJob is { IsTerminal: false })
            {
                settled = true;
                if (command.RetryJobId.HasValue)
                    return Error.Conflict("Import.ServerFolderBusy", "An import is already running from this folder. Try again when it finishes.");
                return new ServerImportResult(ServerImportView.Create(options, clock, previous, lastJob), lastJob.Id);
            }

            // A competing check may have published a newer observation during enumeration.
            if (command.RetryJobId is null && previous?.CheckedAt > startedAt)
            {
                settled = true;
                return new ServerImportResult(ServerImportView.Create(options, clock, previous, lastJob));
            }

            var checkedAt = clock.GetUtcNow();
            IReadOnlyList<ServerImportFile> candidates;
            Guid? correlationId = null;
            int waitingFiles;
            string? warning;
            if (command.RetryJobId is { } retryId)
            {
                // Observe only saved paths after acquiring the same lock as a normal folder
                // check. The worker verifies the accepted versions again before and after copy.
                var review = await retryPlanner.ReviewAsync(directory, retryId, lastJob, ct);
                if (review.IsError)
                    return review.Errors;
                if (!string.Equals(review.Value.Review.Revision, command.ReviewedRevision, StringComparison.Ordinal))
                    return ImportErrors.RetryChanged();

                candidates = review.Value.Candidates;
                if (candidates.Count == 0)
                    return Error.Conflict("Import.NoRetryFiles", "No unchanged originals are ready to retry.");
                correlationId = review.Value.CorrelationId;
                waitingFiles = previous?.WaitingFiles ?? 0;
                warning = null;
            }
            else
            {
                if (scan.IsError)
                    return await FinishCheckAsync(ServerImportCheck.Failed, 0, scan.FirstError.Description);

                var snapshot = scan.Value;
                waitingFiles = snapshot.WaitingFiles;
                warning = snapshot.Warning;
                candidates = await store.FindUnsubmittedFilesAsync(directory, snapshot.Files, ct);
                if (candidates.Count == 0)
                {
                    var check = warning is not null
                        ? ServerImportCheck.Failed
                        : waitingFiles > 0 ? ServerImportCheck.WaitingForFiles : ServerImportCheck.NoNewFiles;
                    return await FinishCheckAsync(check, waitingFiles, warning);
                }
            }

            var capacity = source.ValidateCapacity(candidates);
            if (capacity.IsError)
                return command.RetryJobId.HasValue
                    ? capacity.Errors
                    : await FinishCheckAsync(ServerImportCheck.Failed, waitingFiles, capacity.FirstError.Description);

            string folderName = Path.GetFileName(directory);
            string name = string.IsNullOrEmpty(folderName) ? "Server import folder" : $"{folderName}/";
            var job = UploadJob.Create(name, checkedAt, createdByUserId: command.ActorId, requestId: jobId, batchId: correlationId);
            job.SetImportSource(directory, move: true);
            lease = await exclusion.AcquireAsync(jobId, ct);
            workspace = await preparation.ReserveAsync(jobId, lease, ct);

            var activity = new ServerImportActivity(
                checkedAt, ServerImportCheck.Queued, waitingFiles, warning, jobId, candidates.Count);
            await acceptance.AddStagedAsync(job, ct);
            store.StageSelection(jobId, new ServerImportSelection(candidates));
            await store.StageActivityAsync(directory, activity, ct);
            ct.ThrowIfCancellationRequested();
            commitAttempted = true;
            await transaction.CommitAsync(ct);
            settled = true;
            return new ServerImportResult(ServerImportView.Create(options, clock, activity,
                new ServerImportJob(jobId, UploadPhase.Pending, IsTerminal: false)), jobId);

            async Task<ServerImportResult> FinishCheckAsync(
                ServerImportCheck result, int waiting, string? message)
            {
                var activity = new ServerImportActivity(checkedAt, result, waiting, message, previous?.JobId,
                    previous?.LastQueuedFiles ?? 0);
                await store.StageActivityAsync(directory, activity, ct);
                ct.ThrowIfCancellationRequested();
                commitAttempted = true;
                await transaction.CommitAsync(ct);
                settled = true;
                return new ServerImportResult(ServerImportView.Create(options, clock, activity, lastJob));
            }
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            var cleanup = new JobAcceptanceCleanup(settled || primary is not null,
                (error, resource) => logger.LogWarning(error, "Server import {JobId} {Resource} cleanup failed", jobId, resource));
            await cleanup.DisposeAsync(transaction, "transaction");
            if (!commitAttempted && workspace is not null)
                await cleanup.RunAsync(() => workspace.DiscardAsync(), "unaccepted workspace");
            await cleanup.DisposeAsync(lease, "workspace lease");
            cleanup.ThrowIfFailed("Server folder import cleanup failed.");
        }
    }
}
