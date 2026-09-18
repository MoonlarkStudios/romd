using System.Security.Cryptography;
using ErrorOr;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Artwork.Providers;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Storage;

namespace Romd.Infrastructure.Jobs.Executors;

public sealed class ArtworkImportJobExecutor(
    IArtworkAssetSource source,
    IArtworkImageProcessor processor,
    IContentAddressableStore store,
    IArtworkCurationRepository curation,
    IFileMutationLock fileLocks,
    IUnitOfWork unitOfWork,
    IJobRepository<ArtworkImportJob> jobs) : IResumableJobExecutor<ArtworkImportJob>
{
    public async Task ExecuteAsync(ArtworkImportJob job, JobContext context)
    {
        if (job.RetainedAssetId.HasValue || job.WasSuperseded) return;
        context.CancellationToken.ThrowIfCancellationRequested();
        // The runner durably appends one "attempt" error for each retryable failure.
        // This survives delivery restarts without counting shutdown or lost ownership.
        var failures = job.Errors.Count(error => error.Item == "attempt");
        if (failures >= 3)
        {
            if (await ReconcileOutcomeAsync(job, context)) return;
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
            if (await ReconcileOutcomeAsync(job, context)) return;
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
        if (!persisted.RetainedAssetId.HasValue && !persisted.WasSuperseded) return false;
        job.SetImportOutcome(persisted.RetainedAssetId, persisted.WasSuperseded);
        return true;
    }

    private async Task ExecuteAttemptAsync(ArtworkImportJob job, JobContext context)
    {
        var ct = context.CancellationToken;
        // Publication and its outcome commit together. A crash before the runner's
        // terminal checkpoint must not republish or turn a successful pin into failure.
        if (job.RetainedAssetId.HasValue || job.WasSuperseded) return;
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
        if (downloaded.IsError) ThrowProviderFailure(downloaded.FirstError);
        var processed = await processor.ProcessAsync(downloaded.Value.Bytes, job.Role, ct);
        if (processed.IsError) ThrowProviderFailure(processed.FirstError);
        await context.EnsureExecutionOwnershipAsync(ct);

        // No network or decoding under database locks. Only the selected original
        // and bounded variants enter CAS, after checking the intent a second time.
        int? publishedAssetId = null;
        await context.ExecuteOwnedMutationAsync(async mutationCt =>
        {
            if (!await curation.LockPendingAsync(job, mutationCt))
            {
                await curation.StageOutcomeAsync(job, null, true, mutationCt);
                return;
            }
            var image = processed.Value;
            // Cleanup shares these database-scoped locks. A stale orphan scan or
            // concurrent file-row deletion cannot remove bytes during publication.
            var hashes = image.Variants.Select(variant => Hash(variant.EncodedBytes))
                .Append(Hash(downloaded.Value.Bytes)).Distinct().OrderBy(hash => hash.ToString(), StringComparer.Ordinal);
            foreach (var hash in hashes) await fileLocks.AcquireAsync(hash, mutationCt);
            var original = await StoreAsync(downloaded.Value.Bytes, image.ContentType,
                image.Width, image.Height, "original", mutationCt);
            var variants = new List<RetainedArtworkFile>();
            foreach (var variant in image.Variants)
                variants.Add(await StoreAsync(variant.EncodedBytes, variant.ContentType,
                    variant.Width, variant.Height, variant.Name, mutationCt));
            await curation.StageAssetAsync(job, new RetainedArtworkContent(original, variants,
                job.Attribution, downloaded.Value.SourcePageUrl), mutationCt);
            await unitOfWork.FlushAsync(mutationCt);
            publishedAssetId = await curation.StagePublicationAsync(job, original.Hash.ToString(), mutationCt);
            await curation.StageOutcomeAsync(job, publishedAssetId, false, mutationCt);
            // JobContext's execution guard commits files, effective pin, and outcome together.
        }, ct);
        // Update the runner's in-memory checkpoint only AFTER commit. If publication
        // rolls back, its failure checkpoint must not record a nonexistent success.
        job.SetImportOutcome(publishedAssetId, !publishedAssetId.HasValue);
    }

    private async Task<RetainedArtworkFile> StoreAsync(byte[] bytes, string contentType, int width,
        int height, string name, CancellationToken ct)
    {
        await using var stream = new MemoryStream(bytes, writable: false);
        var result = await store.StoreAsync(stream, null, ct);
        return new RetainedArtworkFile(result.Key.Hash, result.Size, result.CompressedSize,
            result.IsCompressed, contentType, width, height, name);
    }

    private static Sha256 Hash(byte[] bytes) => Sha256.FromSpan(SHA256.HashData(bytes));

    private static void ThrowProviderFailure(Error error)
    {
        if (error.Type is ErrorType.Validation or ErrorType.NotFound || error.Code == "Artwork.ProviderInvalidResponse")
            throw new JobPermanentFailureException(error.Description);
        throw new InvalidOperationException(error.Description);
    }
}
