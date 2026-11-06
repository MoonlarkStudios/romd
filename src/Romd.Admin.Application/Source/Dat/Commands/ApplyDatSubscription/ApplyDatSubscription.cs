using ErrorOr;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Preparation;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Dat;

namespace Romd.Admin.Application.Source.Dat.Commands.ApplyDatSubscription;

public sealed record ApplyDatSubscriptionCommand(int SubscriptionId, string ActiveSha256, string CandidateSha256) : ICommand<Guid>;

public sealed class ApplyDatSubscriptionCommandHandler(IDatSubscriptionCheckReader reader, IDatSubscriptionCheckStore locks,
    IDatSubscriptionApplyStore store, IDatRepository dats, ISignedDatCatalogClient client, IFileStorageService storage,
    IDatReplacementReview review, IReviewedDatReplacementPreparation replacementReview, IJobRepository jobs,
    IJobWorkspaceExclusion exclusion, IUploadWorkspacePreparation uploads, IReplaceDatWorkspacePreparation replacements,
    IUploadJobAcceptance uploadAcceptance, IReplaceDatJobAcceptance replacementAcceptance, IUnitOfWork work,
    ICurrentUser user, TimeProvider clock, ILogger<ApplyDatSubscriptionCommandHandler> logger) : ICommandHandler<ApplyDatSubscriptionCommand, Guid>
{
    public async Task<ErrorOr<Guid>> HandleAsync(ApplyDatSubscriptionCommand command, CancellationToken ct = default)
    {
        ITransaction? transaction = null;
        IJobWorkspaceLease? lease = null;
        Stream? candidate = null;
        Stream? content = null;
        IPreparedDatReplacement? reviewed = null;
        ErrorOr<Guid> outcome = default;
        Exception? primary = null;
        bool durableResult = false;
        Guid? jobId = null;
        try
        {
            // Retain the existing long-I/O transaction and catalog146 -> source145 exclusion.
            // Shortening it requires a separately qualified protocol, not an ownership-only change.
            transaction = await work.BeginTransactionAsync(ct);
            var identity = await reader.ReadAsync(command.SubscriptionId, ct);
            if (identity is null)
                return outcome = DatEnrollmentErrors.NotFound();
            if (!await locks.TryLockCatalogStagedAsync(identity.CatalogId, ct))
                return outcome = DatEnrollmentErrors.Busy();
            var row = await ReadLockedAsync(command.SubscriptionId, ct);
            if (row.Values.DatSourceId is int existingSource)
            {
                if (!await locks.TryLockSourceStagedAsync(existingSource, ct))
                    return outcome = DatEnrollmentErrors.Busy();
                row = await ReadLockedAsync(row.Id, ct);
            }
            if (!PinsMatch(row, command))
                return outcome = DatEnrollmentErrors.Stale();
            if (row.Values.JobId is Guid accepted)
            {
                durableResult = true;
                return outcome = accepted;
            }
            if (!client.Enabled)
                return outcome = DatEnrollmentErrors.Unavailable();
            var active = await ActiveAsync(row, ct);
            if (active is not null)
            {
                if (!await locks.TryLockSourceStagedAsync(active.DatSourceId, ct))
                    return outcome = DatEnrollmentErrors.Busy();
                row = await ReadLockedAsync(row.Id, ct);
                if (!PinsMatch(row, command))
                    return outcome = DatEnrollmentErrors.Stale();
                if (row.Values.JobId is Guid existing)
                {
                    durableResult = true;
                    return outcome = existing;
                }
            }
            var preview = await ReviewAsync(row, ct);
            if (preview.IsError)
                return outcome = preview.Errors;
            candidate = await storage.RetrieveByIdAsync(row.Values.CandidateFileId!.Value, ct);
            if (candidate is null)
                return outcome = DatEnrollmentErrors.Stale();

            if (active is null)
            {
                var actor = user.UserId;
                var job = UploadJob.Create("subscribed-catalog.dat", clock.GetUtcNow(), row.Values.PlatformId, actor);
                jobId = job.Id;
                var existing = await jobs.GetByIdAsync(job.Id, ct);
                if (existing is null)
                {
                    lease = await exclusion.AcquireAsync(job.Id, ct);
                    existing = await jobs.GetByIdAsync(job.Id, ct);
                    if (existing is null)
                    {
                        var prepared = await uploads.PrepareAsync(new(job.Id, job.SourceFilename), candidate, lease, ct);
                        if (prepared.IsError)
                            return outcome = prepared.Errors;
                        await uploadAcceptance.AddStagedAsync(job, ct);
                    }
                }
                if (existing is not null)
                {
                    var reuse = Reuse(existing, job);
                    if (reuse.IsError)
                        return outcome = reuse.Errors;
                    jobId = reuse.Value;
                }
            }
            else
            {
                var prepared = await replacementReview.PrepareAsync(active.Id, candidate, command.ActiveSha256, command.CandidateSha256, ct);
                if (prepared.IsError)
                    return outcome = ServerHeld(prepared.Errors);
                reviewed = prepared.Value;
                content = reviewed.OpenRead();
                // Subscription replacement preserves the legacy null platform override and null actor.
                var job = ReplaceDatJob.Create(active.Id, "reviewed-catalog.dat", clock.GetUtcNow());
                jobId = job.Id;
                lease = await exclusion.AcquireAsync(job.Id, ct);
                await replacements.PrepareAsync(new(job.Id, job.SourceFilename), content, lease, ct);
                await replacementAcceptance.AddStagedAsync(job, ct);
            }

            Guid acceptedJob = jobId ?? throw new InvalidOperationException("Subscription acceptance produced no job identity.");
            await store.StageAcceptedAsync(row.Id, active?.DatSourceId ?? row.Values.DatSourceId, acceptedJob, ct);
            ct.ThrowIfCancellationRequested();
            await transaction.CommitAsync(ct);
            durableResult = true;
            return outcome = acceptedJob;
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            // Retain every completed input on acceptance failure, matching the legacy creation paths.
            // A later rollback or database read cannot establish that an attempted commit was not durable.
            var cleanup = new JobAcceptanceCleanup(
                preserveOutcome: primary is not null || durableResult || outcome.IsError,
                reportFailure: (error, resource) => logger.LogWarning(error,
                    "Subscription application {SubscriptionId} job {JobId} {Resource} cleanup failed after its outcome settled", command.SubscriptionId, jobId, resource));
            await cleanup.DisposeAsync(transaction, "transaction");
            await cleanup.DisposeAsync(content, "reviewed document stream");
            cleanup.Dispose(reviewed, "review receipt");
            await cleanup.DisposeAsync(candidate, "candidate stream");
            await cleanup.DisposeAsync(lease, "workspace lease");
            cleanup.ThrowIfFailed("Subscription application cleanup failed.");
        }
    }

    private async Task<DatSubscriptionCheckSnapshot> ReadLockedAsync(int id, CancellationToken ct) =>
        await reader.ReadAsync(id, ct) ?? throw new InvalidOperationException("The locked subscription no longer exists.");

    private static bool PinsMatch(DatSubscriptionCheckSnapshot row, ApplyDatSubscriptionCommand command) =>
        row.Values.ActiveSha256 == command.ActiveSha256 && row.Values.CandidateSha256 == command.CandidateSha256;

    private async Task<DatFile?> ActiveAsync(DatSubscriptionCheckSnapshot row, CancellationToken ct)
    {
        if (row.Values.DatSourceId is int sourceId)
            return await dats.GetActiveBySourceIdAsync(sourceId, ct);
        if (row.Values.JobId is null || await reader.HasRunningJobAsync(row.Values.JobId, ct) || row.Values.CandidateFileId is not int fileId)
            return null;
        var ids = await reader.ReadActiveDatIdsForDocumentAsync(fileId, row.Values.PlatformId, ct);
        return ids.Count == 1 ? await dats.GetByIdAsync(ids[0], ct) : null;
    }

    private async Task<ErrorOr<DatReplacementPreview>> ReviewAsync(DatSubscriptionCheckSnapshot row, CancellationToken ct)
    {
        if (row.Values.State is not ("ReadyToImport" or "UpdateAvailable") || row.Values.CandidateFileId is not int fileId)
            return DatEnrollmentErrors.Stale();
        var active = await ActiveAsync(row, ct);
        if (row.Values.DatSourceId is not null && active is null)
            return DatEnrollmentErrors.Stale();
        if ((row.Values.PlatformId ?? active?.PlatformId) is not int platformId)
            return DatEnrollmentErrors.PlatformMissing();
        var platform = await store.ReadPlatformAsync(platformId, ct);
        if (platform is null || !platform.Published || platform.Retired)
            return DatEnrollmentErrors.PlatformMissing();
        if (active is not null && (active.PlatformId != platformId || !await reader.IsSourceEnabledAsync(active.DatSourceId, ct)))
            return DatEnrollmentErrors.Stale();
        if (active is null && (await reader.ReadActiveDatIdsForNameAsync(platformId, row.Values.ExpectedName!, ct)).Count != 0)
            return DatEnrollmentErrors.Stale();
        var document = await storage.RetrieveByIdAsync(fileId, ct);
        if (document is null)
            return DatEnrollmentErrors.Stale();
        ErrorOr<DatReplacementPreview> outcome = default;
        Exception? primary = null;
        try
        {
            var result = active is not null ? await review.PreviewAsync(active.Id, document, ct)
                : await review.PreviewInitialAsync(row.Values.ExpectedName ?? active!.Name, document, ct);
            if (!result.IsError && (result.Value.ActiveSha256 != row.Values.ActiveSha256 || result.Value.CandidateSha256 != row.Values.CandidateSha256))
                return outcome = DatEnrollmentErrors.Stale();
            return outcome = result.IsError ? ServerHeld(result.Errors) : result;
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            try
            { await document.DisposeAsync(); }
            catch (Exception error) when (primary is not null || outcome.IsError)
            { logger.LogWarning(error, "Subscription review candidate cleanup failed after its outcome settled"); }
        }
    }

    private static List<Error> ServerHeld(IEnumerable<Error> errors) => errors.Select(error =>
        error.Type == ErrorType.Validation && error.Code is "DatReview.InvalidDocument" or "DatReview.Identity"
            ? Error.Conflict(error.Code, error.Description) : error).ToList();

    private static ErrorOr<Guid> Reuse(Job previous, UploadJob proposed) => previous is UploadJob existing
        && existing.CreatedByUserId == proposed.CreatedByUserId && existing.SourceFilename == proposed.SourceFilename
        && existing.CorrelationId == proposed.CorrelationId && existing.PlatformId == proposed.PlatformId
        && existing.MaxParallelRoms == proposed.MaxParallelRoms && existing.AllowUnidentified == proposed.AllowUnidentified
        && existing.ArchiveOnly == proposed.ArchiveOnly && existing.TrackedOnly == proposed.TrackedOnly
            ? existing.Id : UploadErrors.RequestIdReused();
}
