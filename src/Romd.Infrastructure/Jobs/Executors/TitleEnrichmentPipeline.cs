using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Dashboard;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Storage.Files;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;

namespace Romd.Infrastructure.Jobs.Executors;

public sealed class TitleEnrichmentPipeline(
    ITitleAggregateReader titleAggregates,
    ITitleMetadataStore titleMetadata,
    ITitleRowLock titleLock,
    IPlatformRepository platformRepo,
    IEnrichmentOrchestrator orchestrator,
    IFileStorageService fileStorage,
    IUnitOfWork unitOfWork,
    IStatsNotifier statsNotifier,
    ILibraryMaterializationService materializationService,
    IOptions<EnrichmentOptions> options,
    ILogger<TitleEnrichmentPipeline> logger,
    IAutomaticArtworkService artwork,
    IArtworkCompletionStore completion, TimeProvider clock) : ITitleEnrichmentPipeline
{
    private readonly EnrichmentOptions _options = options.Value;

    public async Task EnrichAsync(int titleId, int? platformId, JobContext context, bool artworkOnly = false)
    {
        var ct = context.CancellationToken;

        var title = await titleAggregates.GetAggregateAsync(titleId, ct)
                    ?? throw new InvalidOperationException($"Title {titleId} not found");

        var platform = platformId.HasValue
            ? await platformRepo.GetByIdAsync(platformId.Value, ct)
            : null;

        if (platform is null)
        {
            throw new InvalidOperationException($"Platform {platformId} not found");
        }

        if (artworkOnly)
        {
            await context.CheckpointAsync(ct);
            await artwork.FillAsync(title, platform, context, ct);
            if (context.DeferredUntil is not null)
                return;
            await statsNotifier.NotifyStorageChangedAsync(ct);
            return;
        }
        await context.CheckpointAsync(ct);
        var originalMediaFileIds = title.Media.Select(m => m.FileId).ToHashSet();
        var originalEligibility = TitleEligibilityFields.From(title);

        await using var result = await orchestrator.EnrichTitleAsync(title, platform, ct);

        if (result.Enriched)
        {
            bool hasConfirmedLink = title.ExternalIds.Any(e => e.IsConfirmed);
            if (hasConfirmedLink || result.BestConfidence >= _options.MinimumAutoEnrichConfidence)
            {
                title.MarkEnrichmentCompleted(clock.GetUtcNow());
            }
            else
            {
                title.MarkEnrichmentLowConfidence(clock.GetUtcNow());
            }
        }
        else if (result.AllErrored)
        {
            title.MarkEnrichmentFailed(clock.GetUtcNow());
        }
        else
        {
            title.MarkEnrichmentNotFound(clock.GetUtcNow());
        }

        await context.ExecuteOwnedMutationAsync(async mutationCt =>
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
            if (originalEligibility != TitleEligibilityFields.From(title))
            {
                await materializationService.FlagAffectedLibrariesAsync(title.PlatformId, mutationCt);
            }

            await titleMetadata.ApplyEnrichmentStagedAsync(title, mutationCt);
            // A step that ran without an execution error succeeded, even when it found nothing. Only a
            // completion run has an item for this title; other jobs record nothing.
            if (context.JobId is { } jobId)
                await completion.RecordMetadataOutcomeAsync(jobId, title.Id,
                    result.AllErrored ? MetadataExecutionOutcome.Failed : MetadataExecutionOutcome.Succeeded, mutationCt);
        }, ct);

        await context.CheckpointAsync(ct);
        await artwork.FillAsync(title, platform, context, ct, result.ArtworkResults);
        if (context.DeferredUntil is not null)
            return;

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
                logger.LogWarning(
                    ex,
                    "Failed to cleanup replaced media file {FileId} for title {TitleId}",
                    fileId,
                    title.Id);
            }
        }

        // Enrichment may have downloaded/replaced media in CAS — refresh storage stats once.
        if (result.Enriched)
        {
            try
            {
                await statsNotifier.NotifyStorageChangedAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to send storage notification after enrichment for title {TitleId}", title.Id);
            }
        }
        // Returns normally → runner calls job.Complete()
    }

}
