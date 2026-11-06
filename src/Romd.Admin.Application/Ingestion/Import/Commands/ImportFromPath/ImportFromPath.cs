using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Preparation;
using Romd.Admin.Application.Titles;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.ReferenceCatalog;
using Romd.Application.Common.Security;
using Romd.Application.Common.Validation;
using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Ingestion.Import.Commands.ImportFromPath;

public sealed record ImportFromPathCommand(string Path, string? SystemKey = null, int MaxParallelRoms = 4,
    bool AllowUnidentified = false, bool ArchiveOnly = false, bool TrackedOnly = false, bool Move = false) : ICommand<Guid>;

public sealed class ImportFromPathCommandHandler(ITrackedTitleReader trackedTitles, IReferenceCatalogService referenceCatalog,
    ICurrentUser user, IPathImportPreparation preparation, IJobWorkspaceExclusion exclusion, IUploadJobAcceptance acceptance,
    IUnitOfWork work, TimeProvider clock, ILogger<ImportFromPathCommandHandler> logger) : ICommandHandler<ImportFromPathCommand, Guid>
{
    public async Task<ErrorOr<Guid>> HandleAsync(ImportFromPathCommand command, CancellationToken ct = default)
    {
        if (command.TrackedOnly && !await trackedTitles.HasTrackedAsync(cancellationToken: ct))
            return FieldErrors.Validation("Upload.NoTrackedTitles", "Track at least one title before importing tracked-title ROMs.", "trackedOnly");

        int? platformId = null;
        if (command.SystemKey is { } key)
        {
            if (string.IsNullOrWhiteSpace(key) || await referenceCatalog.ResolveSystemIdAsync(key, ct) is not { } resolved)
                return FieldErrors.Validation("Systems.UnknownKey", "Not a known system key.", "systemKey");
            platformId = resolved;
        }

        var actor = user.UserId;
        var inspected = preparation.Inspect(command.Path, ct);
        if (inspected.IsError)
            return inspected.Errors;
        var plan = inspected.Value;
        var job = UploadJob.Create(plan.Facts.SourceFilename, clock.GetUtcNow(), platformId, actor);
        job.SetMaxParallelRoms(command.MaxParallelRoms);
        job.SetAllowUnidentified(command.AllowUnidentified);
        job.SetArchiveOnly(command.ArchiveOnly);
        job.SetTrackedOnly(command.TrackedOnly);
        job.SetImportSource(plan.Facts.SourceRoot, command.Move);
        foreach (var issue in plan.Facts.Skipped)
            job.RecordError(issue.Path, issue.Message, clock.GetUtcNow());

        IJobWorkspaceLease? lease = null;
        IPreparedJobWorkspace? prepared = null;
        ITransaction? transaction = null;
        ErrorOr<Guid> outcome = default;
        Exception? primary = null;
        bool commitAttempted = false;
        bool committed = false;
        try
        {
            lease = await exclusion.AcquireAsync(job.Id, ct);
            var copied = await preparation.PrepareAsync(plan, job.Id, command.Move, lease, ct);
            if (copied.IsError)
                return outcome = copied.Errors;
            prepared = copied.Value.Workspace;
            foreach (var issue in copied.Value.CopyIssues)
                job.RecordError(issue.Path, issue.Message, issue.OccurredAt);

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
                    "Path import {JobId} {Resource} cleanup failed after its outcome settled", job.Id, resource));
            await cleanup.DisposeAsync(transaction, "transaction");
            // Once commit was attempted, neither a later rollback nor a negative database read proves
            // publication failed. Keep both copied bytes and the sibling move manifest in that case.
            if (!commitAttempted && prepared is not null)
                await cleanup.RunAsync(() => prepared.DiscardAsync(), "unaccepted workspace");
            await cleanup.DisposeAsync(lease, "workspace lease");
            cleanup.ThrowIfFailed("Path import cleanup failed.");
        }
    }
}
