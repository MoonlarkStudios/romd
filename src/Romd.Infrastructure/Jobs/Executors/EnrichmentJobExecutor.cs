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
using Romd.Domain.Jobs;

namespace Romd.Infrastructure.Jobs.Executors;

public sealed class EnrichmentJobExecutor(
    ITitleRepository titleRepo,
    IPlatformRepository platformRepo,
    IEnrichmentOrchestrator orchestrator,
    IFileStorageService fileStorage,
    IStatsNotifier statsNotifier,
    ILibraryMaterializationService materializationService,
    IOptions<EnrichmentOptions> options,
    ILogger<EnrichmentJobExecutor> logger,
    IAutomaticArtworkService artwork) : IJobExecutor<EnrichmentJob>
{
    private readonly EnrichmentOptions _options = options.Value;

    public async Task ExecuteAsync(EnrichmentJob job, JobContext context)
    {
        var ct = context.CancellationToken;

        var title = await titleRepo.GetWithMetadataLayersAsync(job.TitleId, ct)
                    ?? throw new InvalidOperationException($"Title {job.TitleId} not found");

        var platform = job.PlatformId.HasValue
            ? await platformRepo.GetByIdAsync(job.PlatformId.Value, ct)
            : null;

        if (platform is null)
        {
            throw new InvalidOperationException($"Platform {job.PlatformId} not found");
        }

        if (job.ArtworkOnly)
        {
            job.SetCurrentItem("Finding missing artwork");
            await context.CheckpointAsync(ct);
            await artwork.FillAsync(title, platform, context, ct);
            await statsNotifier.NotifyStorageChangedAsync(ct);
            return;
        }
        job.SetCurrentItem("Refreshing provider data");
        await context.CheckpointAsync(ct);
        var originalMediaFileIds = title.Media.Select(m => m.FileId).ToHashSet();
        var originalEligibility = TitleEligibilityFields.From(title);

        var result = await orchestrator.EnrichTitleAsync(title, platform, ct);

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
        }
        else if (result.AllErrored)
        {
            title.MarkEnrichmentFailed();
        }
        else
        {
            title.MarkEnrichmentNotFound();
        }

        await context.ExecuteOwnedMutationAsync(async mutationCt =>
        {
            if (originalEligibility != TitleEligibilityFields.From(title))
            {
                await materializationService.FlagAffectedLibrariesAsync(title.PlatformId, mutationCt);
            }

            await titleRepo.UpdateAsync(title, mutationCt);
        }, ct);

        job.SetCurrentItem("Filling missing artwork");
        await context.CheckpointAsync(ct);
        await artwork.FillAsync(title, platform, context, ct, result.ArtworkResults);

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
