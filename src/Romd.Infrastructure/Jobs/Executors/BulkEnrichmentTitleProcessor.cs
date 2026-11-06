using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Platform;

namespace Romd.Infrastructure.Jobs.Executors;

/// <summary>
/// Publishes one bulk enrichment result in a short-lived scope. Provider streaming keeps its own
/// lifetime; tracked title, media and artwork rows are released when this processor's scope ends.
/// </summary>
public sealed class BulkEnrichmentTitleProcessor(
    ITitleAggregateReader titleAggregates,
    ITitleMetadataStore titleMetadata,
    ITitleRowLock titleLock,
    IPlatformRepository platformRepo,
    IFileStorageService fileStorage,
    IUnitOfWork unitOfWork,
    IJobNotifier notifier,
    ILibraryMaterializationService materializationService,
    IOptions<EnrichmentOptions> options,
    ILogger<BulkEnrichmentTitleProcessor> logger,
    IAutomaticArtworkService artwork,
    TimeProvider clock,
    IArtworkAcquisitionStore? acquisition = null)
{
    private readonly EnrichmentOptions _options = options.Value;

    public async Task ProcessArtworkAsync(BulkEnrichmentJob job, JobContext context, int titleId)
    {
        var ct = context.CancellationToken;
        var title = await titleAggregates.GetAggregateAsync(titleId, ct);
        var platform = title is null ? null : await platformRepo.GetByIdAsync(title.PlatformId, ct);
        if (title is null || platform is null)
        {
            job.RecordSkipped();
            return;
        }
        job.SetCurrentItem(title.Name);
        await artwork.FillAsync(title, platform, context, ct);
        if (context.DeferredUntil is not null)
            return;
        if (acquisition is not null)
        {
            var outcomes = await acquisition.GetOutcomesAsync(titleId, ct);
            foreach (var outcome in outcomes.Outcomes)
                job.RecordArtworkOutcome(outcome.Role, outcome.Status);
        }
        job.RecordEnriched();
    }

    public async Task ProcessResultAsync(BulkEnrichmentJob job, JobContext context, Title title,
        Platform platform, OrchestrationResult result, IReadOnlySet<int> originalMediaFileIds)
    {
        var ct = context.CancellationToken;
        // Persist identities before acquisition can authorize them. Keep the title
        // nonterminal until artwork finishes so crash recovery revisits this title.
        if (result.Enriched)
        {
            await context.ExecuteOwnedMutationAsync(async token =>
            {
                if (result.StagedMedia.Count > 0)
                {
                    if (!await titleLock.LockAsync(title.Id, token))
                        throw new InvalidOperationException("Title was removed during enrichment.");
                    var publication = await fileStorage.PublishStagedAsync(result.StagedMedia.Select(media => media.File).ToArray(), token);
                    if (publication.RequiresFlush)
                        await unitOfWork.FlushAsync(token);
                    title.AttachPublishedMedia(result.StagedMedia, publication.ReadAfterFlush(), clock.GetUtcNow());
                }
                await titleMetadata.ApplyEnrichmentStagedAsync(title, token);
            }, ct);
            title = await titleAggregates.GetAggregateAsync(title.Id, ct)
                ?? throw new InvalidOperationException("Title was removed during enrichment.");
        }
        await artwork.FillAsync(title, platform, context, ct, result.ArtworkResults);
        if (context.DeferredUntil is not null)
            return;
        var occurredAt = clock.GetUtcNow();
        ApplyTitleResult(title, result, occurredAt);
        await context.ExecuteOwnedMutationAsync(async mutationCt =>
        {
            if (result.Enriched)
                await titleMetadata.UpdateMaterializedMetadataStagedAsync(title, mutationCt);
            else
            {
                if (result.StagedMedia.Count > 0)
                {
                    if (!await titleLock.LockAsync(title.Id, mutationCt))
                        throw new InvalidOperationException("Title was removed during enrichment.");
                    var publication = await fileStorage.PublishStagedAsync(result.StagedMedia.Select(media => media.File).ToArray(), mutationCt);
                    if (publication.RequiresFlush)
                        await unitOfWork.FlushAsync(mutationCt);
                    title.AttachPublishedMedia(result.StagedMedia, publication.ReadAfterFlush(), clock.GetUtcNow());
                }
                await titleMetadata.ApplyEnrichmentStagedAsync(title, mutationCt);
            }
            if (result.Enriched)
            {
                // The enriched title and its authorization-relevant library invalidation
                // are one fenced commit. A crash before the next job checkpoint can then
                // safely skip the already-completed title on redelivery without losing
                // the durable rematerialization intent.
                await materializationService.FlagAffectedLibrariesAsync(platform.Id, mutationCt);
            }
        }, ct);
        // An unacknowledged final write is a processing failure, not an enriched/not-found
        // outcome. The executor records that failure once; only this successful await advances progress.
        RecordResult(job, title, result, occurredAt);

        if (result.Enriched)
        {
            int? coverMediaId = title.Media
                .FirstOrDefault(m => m.Type == MediaType.Cover)?.Id;
            try
            {
                await notifier.NotifyTitleEnrichedAsync(title.Id, platform.Id, coverMediaId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "Failed to notify after enrichment committed for title {TitleId}", title.Id);
            }
        }

        // Cleanup replaced media
        var currentMediaFileIds = title.Media.Select(m => m.FileId).ToHashSet();
        foreach (int fileId in originalMediaFileIds.Except(currentMediaFileIds))
        {
            try
            {
                await fileStorage.DeleteIfUnreferencedAsync(fileId, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to cleanup replaced media file {FileId} for title {TitleId}",
                    fileId, title.Id);
            }
        }
    }

    private void ApplyTitleResult(Title title, OrchestrationResult result, DateTimeOffset occurredAt)
    {
        if (result.Enriched)
        {
            bool hasConfirmedLink = title.ExternalIds.Any(e => e.IsConfirmed);
            if (hasConfirmedLink || result.BestConfidence >= _options.MinimumAutoEnrichConfidence)
            {
                title.MarkEnrichmentCompleted(occurredAt);
            }
            else
            {
                title.MarkEnrichmentLowConfidence(occurredAt);
            }
        }
        else if (result.AllErrored)
        {
            title.MarkEnrichmentFailed(occurredAt);
        }
        else
        {
            title.MarkEnrichmentNotFound(occurredAt);
        }
    }

    private static void RecordResult(BulkEnrichmentJob job, Title title, OrchestrationResult result, DateTimeOffset occurredAt)
    {
        if (result.Enriched)
            job.RecordEnriched();
        else if (result.AllErrored)
            job.RecordFailed(title.Id, title.Name, "All providers failed", JobErrorReason.ProviderError, occurredAt);
        else
            job.RecordNotFound();
    }
}
