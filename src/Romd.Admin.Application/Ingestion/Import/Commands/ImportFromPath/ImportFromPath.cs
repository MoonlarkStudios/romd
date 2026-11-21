using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Preparation;
using Romd.Admin.Application.Titles;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Security;
using Romd.Application.Common.Validation;
using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Ingestion.Import.Commands.ImportFromPath;

public sealed record ImportFromPathCommand(string Path, string? SystemKey = null, int MaxParallelRoms = 4,
    bool AllowUnidentified = false, bool ArchiveOnly = false, bool TrackedOnly = false, bool Move = false, Guid? RequestId = null) : ICommand<Guid>;

public sealed class ImportFromPathCommandHandler(ITrackedTitleReader trackedTitles, IReferenceCatalogService referenceCatalog,
    ICurrentUser user, IPathImportPreparation preparation, IJobWorkspaceExclusion exclusion, IUploadJobAcceptance acceptance,
    IUnitOfWork work, TimeProvider clock, ILogger<ImportFromPathCommandHandler> logger, IJobRepository jobs) : ICommandHandler<ImportFromPathCommand, Guid>
{
    public async Task<ErrorOr<Guid>> HandleAsync(ImportFromPathCommand command, CancellationToken ct = default)
    {
        if (command.RequestId == Guid.Empty)
            return FieldErrors.Validation("Import.InvalidRequestId", "Use a non-empty import request ID.", "requestId");
        Guid jobId = command.RequestId ?? Guid.NewGuid();
        IJobWorkspaceLease? lease = null;
        IPreparedJobWorkspace? prepared = null;
        ITransaction? transaction = null;
        ErrorOr<Guid> outcome = default;
        Exception? primary = null;
        bool commitAttempted = false;
        bool committed = false;
        try
        {
            if (command.RequestId.HasValue)
            {
                // Replay under the same lock as acceptance and worker execution. The source may
                // already be empty after a successful move, so lookup precedes inspection.
                var previous = await jobs.GetByIdAsync(jobId, ct);
                if (previous is not null)
                    return outcome = await ReuseAsync(previous, command, ct);
                lease = await exclusion.AcquireAsync(jobId, ct);
                previous = await jobs.GetByIdAsync(jobId, ct);
                if (previous is not null)
                    return outcome = await ReuseAsync(previous, command, ct);
            }

            if (command.TrackedOnly && !await trackedTitles.HasTrackedAsync(cancellationToken: ct))
                return outcome = FieldErrors.Validation("Upload.NoTrackedTitles", "Track at least one title before importing tracked-title ROMs.", "trackedOnly");

            int? platformId = null;
            if (command.SystemKey is { } key)
            {
                if (string.IsNullOrWhiteSpace(key) || await referenceCatalog.ResolveSystemIdAsync(key, ct) is not { } resolved)
                    return outcome = FieldErrors.Validation("Systems.UnknownKey", "Not a known system key.", "systemKey");
                platformId = resolved;
            }

            var actor = user.UserId;
            // Inspection validates the source, file count and workspace capacity so the request fails
            // fast. The worker re-inspects and copies it during the job's Copying phase: acceptance
            // never waits for the bytes, which can take far longer than a proxy allows a request.
            var inspected = preparation.Inspect(command.Path, ct);
            if (inspected.IsError)
                return outcome = inspected.Errors;
            var plan = inspected.Value;
            var job = UploadJob.Create(plan.Facts.SourceFilename, clock.GetUtcNow(), platformId, actor, requestId: jobId);
            job.SetMaxParallelRoms(command.MaxParallelRoms);
            job.SetAllowUnidentified(command.AllowUnidentified);
            job.SetArchiveOnly(command.ArchiveOnly);
            job.SetTrackedOnly(command.TrackedOnly);
            job.SetImportSource(plan.Facts.SourceRoot, command.Move);

            lease ??= await exclusion.AcquireAsync(jobId, ct);
            prepared = await preparation.ReserveAsync(job.Id, lease, ct);

            transaction = await work.BeginTransactionAsync(ct);
            await acceptance.AddStagedAsync(job, ct);
            ct.ThrowIfCancellationRequested();
            // Commit also flushes the GUID job and its dispatch; no generated identity read is needed.
            commitAttempted = true;
            await transaction.CommitAsync(ct);
            committed = true;
            return outcome = job.Id;
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            var cleanup = new JobAcceptanceCleanup(
                preserveOutcome: primary is not null || committed || outcome.IsError,
                reportFailure: (error, resource) => logger.LogWarning(error,
                    "Path import {JobId} {Resource} cleanup failed after its outcome settled", jobId, resource));
            await cleanup.DisposeAsync(transaction, "transaction");
            // Once commit was attempted, neither a later rollback nor a negative database read proves
            // publication failed. Keep the reserved workspace in that case; orphan cleanup owns it.
            if (!commitAttempted && prepared is not null)
                await cleanup.RunAsync(() => prepared.DiscardAsync(), "unaccepted workspace");
            await cleanup.DisposeAsync(lease, "workspace lease");
            cleanup.ThrowIfFailed("Path import cleanup failed.");
        }
    }

    private async Task<ErrorOr<Guid>> ReuseAsync(Job previous, ImportFromPathCommand command, CancellationToken ct)
    {
        if (previous is not UploadJob existing || existing.CreatedByUserId != user.UserId ||
            existing.ImportSourcePath is not { } storedPath || !SameSource(storedPath, command.Path) ||
            existing.ImportMove != command.Move || existing.AllowUnidentified != command.AllowUnidentified ||
            existing.ArchiveOnly != command.ArchiveOnly || existing.TrackedOnly != command.TrackedOnly ||
            existing.MaxParallelRoms != command.MaxParallelRoms)
            return UploadErrors.RequestIdReused();
        if (command.SystemKey is null)
            return existing.PlatformId is null ? existing.Id : UploadErrors.RequestIdReused();
        if (!string.IsNullOrWhiteSpace(command.SystemKey) &&
            await referenceCatalog.ResolveSystemIdAsync(command.SystemKey, ct) is { } platform &&
            existing.PlatformId == platform)
            return existing.Id;
        return UploadErrors.RequestIdReused();
    }

    private static bool SameSource(string stored, string requested)
    {
        if (string.IsNullOrEmpty(requested) || !Path.IsPathFullyQualified(requested))
            return false;
        try
        {
            return string.Equals(stored, Path.TrimEndingDirectorySeparator(Path.GetFullPath(requested)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
