using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Preparation;
using Romd.Application.Common.Configuration;
using Romd.Application.Common.Cqrs;
using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Ingestion.Import.Commands.StartServerImport;

public sealed record StartServerImportCommand(Guid? ActorId, Guid? RetryJobId = null) : ICommand<ServerImportResult>;

public sealed record ServerImportResult(ServerImportView Status, Guid? AcceptedJobId = null);

public sealed class StartServerImportCommandHandler(
    IServerImportOptions options, IServerImportSource source, IServerImportStore store, IUnitOfWork work,
    IJobWorkspaceExclusion exclusion, IPathImportPreparation preparation, IUploadJobAcceptance acceptance,
    TimeProvider clock, ILogger<StartServerImportCommandHandler> logger)
    : ICommandHandler<StartServerImportCommand, ServerImportResult>
{
    public async Task<ErrorOr<ServerImportResult>> HandleAsync(
        StartServerImportCommand command, CancellationToken ct = default)
    {
        if (options.ServerImportDirectory is not { } directory)
            return Error.NotFound("Import.ServerDirectoryDisabled", "A server import folder has not been configured.");

        if (command.RetryJobId is null)
        {
            var activity = await store.ReadActivityAsync(directory, ct);
            if (await store.ReadJobAsync(activity?.JobId, ct) is { IsTerminal: false } running)
                return new ServerImportResult(ServerImportView.Create(options, clock, activity, running), running.Id);
        }

        var startedAt = clock.GetUtcNow();
        var scan = source.Scan(directory, startedAt, ct);
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
            if (command.RetryJobId is { } retryId && (lastJob is not { CanRetry: true } || lastJob.Id != retryId))
            {
                settled = true;
                return Error.Conflict("Import.ServerRetryChanged", "Refresh the folder status before retrying its last import.");
            }

            if (lastJob is { IsTerminal: false })
            {
                settled = true;
                return new ServerImportResult(ServerImportView.Create(options, clock, previous, lastJob), lastJob.Id);
            }

            // A competing check may have published a newer observation during enumeration.
            if (command.RetryJobId is null && previous?.CheckedAt > startedAt)
            {
                settled = true;
                return new ServerImportResult(ServerImportView.Create(options, clock, previous, lastJob));
            }

            var checkedAt = clock.GetUtcNow();
            if (scan.IsError)
                return await FinishCheckAsync(ServerImportCheck.Failed, 0, scan.FirstError.Description);

            var snapshot = scan.Value;
            var seen = (await store.ReadSubmittedFilesAsync(directory, ct)).ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
            IReadOnlyList<ServerImportFile> candidates;
            if (command.RetryJobId is { } retry)
            {
                var old = await store.ReadSelectionAsync(retry, ct);
                var retryFiles = old?.Files.ToHashSet() ?? [];
                candidates = snapshot.Files.Where(retryFiles.Contains).ToArray();
            }
            else
            {
                candidates = snapshot.Files.Where(file => !seen.TryGetValue(file.RelativePath, out var earlier) || earlier != file).ToArray();
            }

            IReadOnlyList<ServerImportFile>? submitted = null;
            if (snapshot.Warning is null)
            {
                var present = snapshot.Files.Select(file => file.RelativePath).ToHashSet(StringComparer.Ordinal);
                if (seen.Keys.Any(path => !present.Contains(path)))
                    submitted = seen.Values.Where(file => present.Contains(file.RelativePath)).ToArray();
            }

            if (candidates.Count == 0)
            {
                string? message = command.RetryJobId.HasValue
                    ? "The files from that import are no longer ready in this folder."
                    : snapshot.Warning;
                var check = snapshot.Warning is not null
                    ? ServerImportCheck.Failed
                    : snapshot.WaitingFiles > 0
                        ? ServerImportCheck.WaitingForFiles
                        : ServerImportCheck.NoNewFiles;
                return await FinishCheckAsync(check, snapshot.WaitingFiles, message, submitted);
            }

            var capacity = source.ValidateCapacity(candidates);
            if (capacity.IsError)
                return await FinishCheckAsync(ServerImportCheck.Failed, snapshot.WaitingFiles, capacity.FirstError.Description);

            string folderName = Path.GetFileName(directory);
            string name = string.IsNullOrEmpty(folderName) ? "Server import folder" : $"{folderName}/";
            var job = UploadJob.Create(name, checkedAt, createdByUserId: command.ActorId, requestId: jobId);
            job.SetImportSource(directory, move: false);
            lease = await exclusion.AcquireAsync(jobId, ct);
            workspace = await preparation.ReserveAsync(jobId, lease, ct);

            if (submitted is not null)
                seen = submitted.ToDictionary(file => file.RelativePath, StringComparer.Ordinal);
            foreach (var file in candidates)
                seen[file.RelativePath] = file;

            var activity = new ServerImportActivity(
                checkedAt, ServerImportCheck.Queued, snapshot.WaitingFiles, snapshot.Warning, jobId, candidates.Count);
            await acceptance.AddStagedAsync(job, ct);
            store.StageSelection(jobId, new ServerImportSelection(candidates));
            await store.StageActivityAsync(directory, activity, seen.Values.ToArray(), ct);
            ct.ThrowIfCancellationRequested();
            commitAttempted = true;
            await transaction.CommitAsync(ct);
            settled = true;
            return new ServerImportResult(ServerImportView.Create(options, clock, activity,
                new ServerImportJob(jobId, UploadPhase.Pending, IsTerminal: false)), jobId);

            async Task<ServerImportResult> FinishCheckAsync(
                ServerImportCheck result, int waiting, string? message, IReadOnlyList<ServerImportFile>? files = null)
            {
                var activity = new ServerImportActivity(checkedAt, result, waiting, message, previous?.JobId,
                    previous?.LastQueuedFiles ?? 0);
                await store.StageActivityAsync(directory, activity, files, ct);
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
