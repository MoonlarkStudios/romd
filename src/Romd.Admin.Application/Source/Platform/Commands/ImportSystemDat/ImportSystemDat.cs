using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Preparation;
using Romd.Admin.Application.Source.Dat;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;

namespace Romd.Admin.Application.Source.Platform.Commands.ImportSystemDat;

/// <summary>The endpoint owns the document stream; this owner never disposes it.</summary>
public sealed record ImportSystemDatCommand(int PlatformId, Stream Document, string Sha256) : ICommand<Guid>;

public sealed class ImportSystemDatCommandHandler(IDatReplacementReview review, ISystemDatImportStore systems,
    IJobRepository jobs, IJobWorkspaceExclusion exclusion, IUploadWorkspacePreparation preparation,
    IUploadJobAcceptance acceptance, IUnitOfWork work, ICurrentUser user, TimeProvider clock,
    ILogger<ImportSystemDatCommandHandler> logger) : ICommandHandler<ImportSystemDatCommand, Guid>
{
    public async Task<ErrorOr<Guid>> HandleAsync(ImportSystemDatCommand command, CancellationToken ct = default)
    {
        var inspected = await review.InspectAsync(command.Document, ct);
        if (inspected.IsError)
            return inspected.Errors;
        if (inspected.Value.Preview.CandidateSha256 != command.Sha256)
            return Error.Conflict("SystemSetup.Stale", "The selected document changed. Review it again.");

        ITransaction? transaction = null;
        IJobWorkspaceLease? lease = null;
        ErrorOr<Guid> outcome = default;
        Exception? primary = null;
        bool committed = false;
        Guid? jobId = null;
        try
        {
            // Retained System Import exception: enable/missing/installed-document precedence requires
            // the legacy workspace copy inside this transaction. See backend-mutations.md.
            transaction = await work.BeginTransactionAsync(ct);
            if (!await systems.EnableStagedAsync(command.PlatformId, ct))
                return outcome = Error.NotFound("SystemSetup.NotFound", "This system no longer exists.");
            if (await systems.IsDocumentInstalledAsync(Sha256.Parse(command.Sha256), ct))
                return outcome = Error.Conflict("SystemSetup.Unchanged", "This exact DAT document is already installed. Open its existing source; no new import is needed.");

            // An explicit source import never chooses an existing source by its display name.
            command.Document.Position = 0;
            var actor = user.UserId;
            var job = UploadJob.Create("reviewed-catalog.dat", clock.GetUtcNow(), command.PlatformId, actor);
            jobId = job.Id;
            var existing = await jobs.GetByIdAsync(job.Id, ct);
            if (existing is null)
            {
                lease = await exclusion.AcquireAsync(job.Id, ct);
                existing = await jobs.GetByIdAsync(job.Id, ct);
                if (existing is null)
                {
                    var prepared = await preparation.PrepareAsync(new(job.Id, job.SourceFilename), command.Document, lease, ct);
                    if (prepared.IsError)
                        return outcome = prepared.Errors;
                    await acceptance.AddStagedAsync(job, ct);
                    outcome = job.Id;
                }
            }
            if (existing is not null)
                outcome = Reuse(existing, job);
            if (outcome.IsError)
                return outcome;

            await work.FlushAsync(ct);
            ct.ThrowIfCancellationRequested();
            await transaction.CommitAsync(ct);
            committed = true;
            return outcome;
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            // Settle and dispose the transaction before releasing the same physical worker/collector
            // lease. Retain completed input for every failed or uncertain acceptance; cleanup never
            // infers rollback from a later successful Rollback/Dispose call.
            var cleanup = new JobAcceptanceCleanup(
                preserveOutcome: primary is not null || committed || outcome.IsError,
                reportFailure: (error, resource) => logger.LogWarning(error,
                    "System DAT import {JobId} {Resource} cleanup failed after its outcome settled", jobId, resource));
            await cleanup.DisposeAsync(transaction, "transaction");
            await cleanup.DisposeAsync(lease, "workspace lease");
            cleanup.ThrowIfFailed("System DAT import cleanup failed.");
        }
    }

    private static ErrorOr<Guid> Reuse(Job previous, UploadJob proposed) => previous is UploadJob existing
        && existing.CreatedByUserId == proposed.CreatedByUserId && existing.SourceFilename == proposed.SourceFilename
        && existing.CorrelationId == proposed.CorrelationId && existing.PlatformId == proposed.PlatformId
        && existing.MaxParallelRoms == proposed.MaxParallelRoms && existing.AllowUnidentified == proposed.AllowUnidentified
        && existing.ArchiveOnly == proposed.ArchiveOnly && existing.TrackedOnly == proposed.TrackedOnly
            ? existing.Id : UploadErrors.RequestIdReused();
}
