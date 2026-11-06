using ErrorOr;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Jobs;

namespace Romd.Infrastructure.Jobs.Executors;

public sealed class ArtworkImportJobExecutor(
    IArtworkAssetSource source,
    IArtworkImageProcessor processor,
    IFileStorageService files,
    IArtworkCurationRepository curation,
    IUnitOfWork unitOfWork,
    IJobRepository<ArtworkImportJob> jobs) : IResumableJobExecutor<ArtworkImportJob>
{
    public async Task ExecuteAsync(ArtworkImportJob job, JobContext context)
    {
        if (job.RetainedAssetId.HasValue || job.WasSuperseded)
            return;
        context.CancellationToken.ThrowIfCancellationRequested();
        // The runner durably appends one "attempt" error for each retryable failure.
        // This survives delivery restarts without counting shutdown or lost ownership.
        var failures = job.Errors.Count(error => error.Item == "attempt");
        if (failures >= 3)
        {
            if (await ReconcileOutcomeAsync(job, context))
                return;
            throw new JobPermanentFailureException("Artwork import stopped after three failed attempts.");
        }
        try
        {
            await ExecuteAttemptAsync(job, context);
        }
        catch (OperationCanceledException) { throw; }
        catch (JobExecutionOwnershipLostException) { throw; }
        catch (JobPermanentFailureException) { throw; }
        catch (Exception) when (failures >= 2)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (await ReconcileOutcomeAsync(job, context))
                return;
            throw new JobPermanentFailureException("Artwork import stopped after three failed attempts. Your previous selection was preserved.");
        }
    }

    private async Task<bool> ReconcileOutcomeAsync(ArtworkImportJob job, JobContext context)
    {
        var ct = context.CancellationToken;
        await context.EnsureExecutionOwnershipAsync(ct);
        // A commit may succeed while its acknowledgement fails. Only a fresh successful
        // read can prove that no durable publication occurred. Read failures propagate
        // for redelivery instead of converting an uncertain commit into terminal failure.
        var persisted = await jobs.GetByIdAsync(job.Id, ct)
            ?? throw new InvalidOperationException("The durable artwork import outcome could not be verified.");
        await context.EnsureExecutionOwnershipAsync(ct);
        if (!persisted.RetainedAssetId.HasValue && !persisted.WasSuperseded)
            return false;
        job.SetImportOutcome(persisted.RetainedAssetId, persisted.WasSuperseded);
        return true;
    }

    private async Task ExecuteAttemptAsync(ArtworkImportJob job, JobContext context)
    {
        var ct = context.CancellationToken;
        // Publication and its outcome commit together. A crash before the runner's
        // terminal checkpoint must not republish or turn a successful pin into failure.
        if (job.RetainedAssetId.HasValue || job.WasSuperseded)
            return;
        if (job.PhaseEnum != ArtworkImportPhase.Importing)
            throw new InvalidOperationException("Artwork import is not in its resumable phase.");

        var pending = false;
        await context.ExecuteOwnedMutationAsync(async mutationCt =>
        {
            pending = await curation.LockPendingAsync(job, mutationCt);
            if (!pending)
            {
                await curation.StageOutcomeAsync(job, null, true, mutationCt);
            }
        }, ct);
        if (!pending)
        {
            job.SetImportOutcome(null, true);
            return;
        }

        var downloaded = await source.DownloadAsync(job.ProviderId, job.ProviderGameId,
            job.ProviderAssetId, job.Role, job.TrustedAssetUrl, ct);
        if (downloaded.IsError)
            ThrowProviderFailure(downloaded.FirstError);
        var processed = await processor.ProcessAsync(downloaded.Value.Bytes, job.Role, ct);
        if (processed.IsError)
            ThrowProviderFailure(processed.FirstError);
        await context.EnsureExecutionOwnershipAsync(ct);

        // No network, decoding or compression under database locks. Only the selected original
        // and bounded variants enter CAS, after checking the intent a second time.
        await using var staged = await PreparedArtwork.StageAsync(files, downloaded.Value.Bytes, processed.Value, ct);
        int? publishedAssetId = null;
        await context.ExecuteOwnedMutationAsync(async mutationCt =>
        {
            if (!await curation.LockPendingAsync(job, mutationCt))
            {
                await curation.StageOutcomeAsync(job, null, true, mutationCt);
                return;
            }
            // The ledger holds file locks, which cleanup shares, from publication through the commit.
            var publication = await files.PublishStagedAsync(staged.Files, mutationCt);
            if (publication.RequiresFlush)
                await unitOfWork.FlushAsync(mutationCt);
            var content = staged.Retain(publication.ReadAfterFlush(), job.Attribution, downloaded.Value.SourcePageUrl);
            await curation.StageAssetAsync(job, content, mutationCt);
            await unitOfWork.FlushAsync(mutationCt);
            publishedAssetId = await curation.StagePublicationAsync(job, content.Original.Hash.ToString(), mutationCt);
            await curation.StageOutcomeAsync(job, publishedAssetId, false, mutationCt);
            // JobContext's execution guard commits files, effective pin, and outcome together.
        }, ct);
        // Update the runner's in-memory checkpoint only AFTER commit. If publication
        // rolls back, its failure checkpoint must not record a nonexistent success.
        job.SetImportOutcome(publishedAssetId, !publishedAssetId.HasValue);
    }


    private static void ThrowProviderFailure(Error error)
    {
        if (error.Type is ErrorType.Validation or ErrorType.NotFound ||
            error.Code is "Artwork.ProviderInvalidResponse" or "Artwork.ProviderNotConfigured")
            throw new JobPermanentFailureException(error.Description);
        throw new InvalidOperationException(error.Description);
    }
}
