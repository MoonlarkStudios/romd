using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Titles;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;
namespace Romd.Infrastructure.Jobs.Executors;

public sealed class ArtworkCompletionJobExecutor(IArtworkCompletionStore store, ITitleAggregateReader titles,
    IPlatformRepository platforms, IAutomaticArtworkService artwork, ILogger<ArtworkCompletionJobExecutor> logger, TimeProvider clock, ITitleEnrichmentPipeline? enrichment = null) : IJobExecutor<ArtworkCompletionJob>
{
    public async Task ExecuteAsync(ArtworkCompletionJob job, JobContext context)
    {
        var ct = context.CancellationToken;
        while (await store.NextAsync(job.Id, ct) is { } item)
        {
            ct.ThrowIfCancellationRequested();
            job.SetCurrentItem(item.TitleName);
            var outcomes = new Dictionary<ArtworkRole, ArtworkCompletionOutcome>();
            // The pipeline records a metadata step that finished; this fallback marks one that threw (Failed) or
            // never ran (NotAttempted), and never lowers what the pipeline recorded.
            var metadata = MetadataExecutionOutcome.NotAttempted;
            var enriching = false;
            try
            {
                var title = await titles.GetAggregateAsync(item.TitleId, ct);
                var platform = title is null ? null : await platforms.GetByIdAsync(title.PlatformId, ct);
                if (title is null || platform is null)
                    Fill(ArtworkCompletionOutcome.Removed);
                else if (item.RequireTracked && !await store.IsTrackedAsync(item.TitleId, ct))
                    Fill(ArtworkCompletionOutcome.Untracked);
                else
                {
                    if (item.Mode == "Enrich")
                    {
                        enriching = true;
                        await (enrichment ?? throw new InvalidOperationException("Enrichment pipeline is required.")).EnrichAsync(title.Id, title.PlatformId, context);
                    }
                    else
                        await artwork.FillAsync(title, platform, context, ct);
                    if (context.DeferredUntil is not null)
                        return;
                    var result = await store.OutcomesAsync(job.Id, item.TitleId, ct);
                    foreach (var role in Enum.GetValues<ArtworkRole>())
                    {
                        outcomes[role] = result.GetValueOrDefault(role, ArtworkCompletionOutcome.Failed);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (JobExecutionOwnershipLostException) { throw; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Artwork completion {JobId} failed for title {TitleId}", job.Id, item.TitleId);
                Fill(ArtworkCompletionOutcome.Failed);
                if (enriching)
                    metadata = MetadataExecutionOutcome.Failed;
            }
            await store.CompleteAsync(job.Id, item.TitleId, outcomes, metadata, context, ct);
            job.SetProgress(await store.CompletedCountAsync(job.Id, ct));
            await context.CheckpointAsync(ct);
            void Fill(ArtworkCompletionOutcome value)
            { foreach (var role in Enum.GetValues<ArtworkRole>()) outcomes[role] = value; }
        }
        job.SetProgress(await store.CompletedCountAsync(job.Id, ct));
        if (await store.HasFailuresAsync(job.Id, ct) && !job.HasErrors)
            job.RecordError("Artwork completion", "Some titles could not be processed. Review the per-title results and retry failures.", occurredAt: clock.GetUtcNow());
    }
}
