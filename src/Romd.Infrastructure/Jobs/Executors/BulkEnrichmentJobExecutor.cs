using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Dashboard;
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

public sealed class BulkEnrichmentJobExecutor(
    ITitleRepository titleRepo,
    IPlatformRepository platformRepo,
    IEnrichmentOrchestrator orchestrator,
    IFileStorageService fileStorage,
    IJobNotifier notifier,
    IStatsNotifier statsNotifier,
    ILibraryMaterializationService materializationService,
    IOptions<EnrichmentOptions> options,
    ILogger<BulkEnrichmentJobExecutor> logger,
    IAutomaticArtworkService artwork) : IJobExecutor<BulkEnrichmentJob>
{
    private readonly EnrichmentOptions _options = options.Value;

    public async Task ExecuteAsync(BulkEnrichmentJob job, JobContext context)
    {
        var ct = context.CancellationToken;

        var platform = job.PlatformId.HasValue
            ? await platformRepo.GetByIdAsync(job.PlatformId.Value, ct)
            : null;

        if (platform is null)
        {
            throw new InvalidOperationException($"Platform {job.PlatformId} not found");
        }

        var titles = await titleRepo.GetNeedingEnrichmentByPlatformAsync(platform.Id, job.Scope, ct);

        job.SetTotalTitles(titles.Count);
        await context.CheckpointAsync(ct);

        if (titles.Count == 0)
        {
            logger.LogInformation(
                "Bulk enrichment for platform {Platform}: no titles need enrichment",
                platform.ShortName);
            return;
        }

        logger.LogInformation(
            "Bulk enrichment for platform {Platform}: processing {Count} titles",
            platform.ShortName, titles.Count);

        // Sort: owned titles first (prioritize titles with ROM files)
        var ownedIds = await titleRepo.GetTitleIdsWithLocalPayloadAsync(
            titles.Select(t => t.Id), ct);

        var sortedTitles = titles
            .OrderByDescending(t => ownedIds.Contains(t.Id))
            .ThenBy(t => t.Name)
            .ToList();

        // Populated by LoadAndFilterTitlesAsync as titles are yielded into the stream
        var loadedTitles = new Dictionary<int, Title>();
        var originalMediaFileIds = new Dictionary<int, HashSet<int>>();

        int processedSinceCheckpoint = 0;

        // Stream all titles through the orchestrator — each provider handles its own batching
        await foreach ((int titleId, var result) in orchestrator.EnrichStreamAsync(
                           LoadAndFilterTitlesAsync(job, sortedTitles, platform, loadedTitles, originalMediaFileIds,
                               ct), ct))
        {
            if (!loadedTitles.TryGetValue(titleId, out var title))
            {
                continue;
            }

            try
            {
                // Fill first: a worker restart must not skip artwork because metadata
                // was marked completed before acquisition finished.
                await artwork.FillAsync(title, platform, context, ct, result.ArtworkResults);
                ApplyResult(job, title, result);
                await context.ExecuteOwnedMutationAsync(async mutationCt =>
                {
                    await titleRepo.UpdateAsync(title, mutationCt);
                    if (result.Enriched)
                    {
                        // The enriched title and its authorization-relevant library invalidation
                        // are one fenced commit. A crash before the next job checkpoint can then
                        // safely skip the already-completed title on redelivery without losing
                        // the durable rematerialization intent.
                        await materializationService.FlagAffectedLibrariesAsync(platform.Id, mutationCt);
                    }
                }, ct);

                if (result.Enriched)
                {
                    int? coverMediaId = title.Media
                        .FirstOrDefault(m => m.Type == MediaType.Cover)?.Id;
                    await notifier.NotifyTitleEnrichedAsync(title.Id, platform.Id, coverMediaId, ct);
                }

                // Cleanup replaced media
                if (originalMediaFileIds.TryGetValue(titleId, out var origIds))
                {
                    var currentMediaFileIds = title.Media.Select(m => m.FileId).ToHashSet();
                    foreach (int fileId in origIds.Except(currentMediaFileIds))
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
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (JobExecutionOwnershipLostException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to process enrichment result for title '{TitleName}' (ID: {TitleId})",
                    title.Name, title.Id);
                job.RecordFailed(title.Id, title.Name, ex.Message, JobErrorReason.ProcessingError);
            }

            processedSinceCheckpoint++;
            if (processedSinceCheckpoint >= _options.PersistenceBatchSize)
            {
                await context.CheckpointAsync(ct);
                processedSinceCheckpoint = 0;
            }
        }

        // Final checkpoint
        if (processedSinceCheckpoint > 0)
        {
            await context.CheckpointAsync(CancellationToken.None);
        }

        logger.LogInformation(
            "Bulk enrichment for platform {Platform} complete: {Enriched} enriched, {NotFound} not found, {Failed} failed, {Skipped} skipped",
            platform.ShortName, job.EnrichedCount, job.NotFoundCount, job.FailedCount, job.SkippedCount);

        // Flag affected libraries for rematerialization after enrichment (genre/rating may change)
        if (job.EnrichedCount > 0)
        {
            // Enrichment downloaded/replaced media in CAS — refresh storage stats once for the whole job.
            try
            {
                await statsNotifier.NotifyStorageChangedAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to send storage notification after bulk enrichment for platform {Platform}", platform.ShortName);
            }
        }
    }

    private async IAsyncEnumerable<(Title Title, Platform Platform)> LoadAndFilterTitlesAsync(
        BulkEnrichmentJob job,
        List<Title> sortedTitles,
        Platform platform,
        Dictionary<int, Title> loadedTitles,
        Dictionary<int, HashSet<int>> originalMediaFileIds,
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var stub in sortedTitles)
        {
            job.SetCurrentItem(stub.Name);

            var title = await titleRepo.GetWithMetadataLayersAsync(stub.Id, ct);
            if (title is null)
            {
                job.RecordSkipped();
                continue;
            }

            if (title.EnrichmentStatus is EnrichmentStatus.Completed or EnrichmentStatus.NotFound)
            {
                job.RecordSkipped();
                continue;
            }

            loadedTitles[title.Id] = title;
            originalMediaFileIds[title.Id] = title.Media.Select(m => m.FileId).ToHashSet();
            yield return (title, platform);
        }
    }

    private void ApplyResult(BulkEnrichmentJob job, Title title, OrchestrationResult result)
    {
        if (result.Enriched)
        {
            bool hasConfirmedLink = title.ExternalIds.Any(e => e.IsConfirmed);
            if (hasConfirmedLink || result.BestConfidence >= _options.MinimumAutoEnrichConfidence)
            {
                title.MarkEnrichmentCompleted();
            }
            else
            {
                title.MarkEnrichmentLowConfidence();
            }

            job.RecordEnriched();
        }
        else if (result.AllErrored)
        {
            title.MarkEnrichmentFailed();
            job.RecordFailed(title.Id, title.Name, "All providers failed", JobErrorReason.ProviderError);
        }
        else
        {
            title.MarkEnrichmentNotFound();
            job.RecordNotFound();
        }
    }
}
