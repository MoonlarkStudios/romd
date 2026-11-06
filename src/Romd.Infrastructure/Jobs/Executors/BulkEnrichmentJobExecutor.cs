using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Romd.Admin.Application.Configuration;
using Romd.Admin.Application.Dashboard;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Platform;
using Romd.Admin.Application.Titles;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Admin.Application.Titles.ReadModels;
using Romd.Application.Common.Security;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Platform;

namespace Romd.Infrastructure.Jobs.Executors;

public sealed class BulkEnrichmentJobExecutor(
    ITitleAggregateReader titleAggregates,
    ITitleEnrichmentStatusReader enrichmentStatus,
    IPlatformRepository platformRepo,
    IEnrichmentOrchestrator orchestrator,
    IStatsNotifier statsNotifier,
    IOptions<EnrichmentOptions> options,
    ILogger<BulkEnrichmentJobExecutor> logger,
    IServiceScopeFactory scopes, TimeProvider clock) : IJobExecutor<BulkEnrichmentJob>
{
    private readonly EnrichmentOptions _options = options.Value;

    public async Task ExecuteAsync(BulkEnrichmentJob job, JobContext context)
    {
        var ct = context.CancellationToken;

        if (job.ArtworkTitleIds is { } ids)
        {
            foreach (var id in ids.Skip(job.ProcessedCount))
            {
                ct.ThrowIfCancellationRequested();
                await InTitleScopeAsync(job, context,
                    (processor, owned) => processor.ProcessArtworkAsync(job, owned, id));
                if (context.DeferredUntil is not null)
                    return;
                await context.CheckpointAsync(ct);
            }
            return;
        }

        var platform = job.PlatformId.HasValue
            ? await platformRepo.GetByIdAsync(job.PlatformId.Value, ct)
            : null;

        if (platform is null)
        {
            throw new InvalidOperationException($"Platform {job.PlatformId} not found");
        }

        var titles = await enrichmentStatus.GetCandidatesAsync(platform.Id, job.Scope, ct);

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
        var sortedTitles = titles
            .OrderByDescending(t => t.HasLocalPayload)
            .ThenBy(t => t.Name)
            .ToList();

        // Keep only titles still awaiting a result from the orchestrator's current window.
        var pendingTitles = new Dictionary<int, (Title Title, HashSet<int> OriginalMediaFileIds)>();

        int processedSinceCheckpoint = 0;

        // Stream all titles through the orchestrator — each provider handles its own batching
        await foreach ((int titleId, var result) in orchestrator.EnrichStreamAsync(
                           LoadAndFilterTitlesAsync(job, sortedTitles, platform, pendingTitles, ct), ct))
        {
            await using var ownedResult = result;
            // Transfer both the aggregate and cleanup snapshot out of the pending window before
            // processing. Success, failure, and artwork deferral must not retain finished titles.
            if (!pendingTitles.Remove(titleId, out var pending))
            {
                continue;
            }
            var title = pending.Title;
            // Results are processed sequentially. A later scope cleanup failure must not
            // count an acknowledged result a second time as a processing failure.
            int processedBefore = job.ProcessedCount;

            try
            {
                await InTitleScopeAsync(job, context,
                    (processor, owned) => processor.ProcessResultAsync(job, owned, title, platform, result,
                        pending.OriginalMediaFileIds));
                if (context.DeferredUntil is not null)
                    return;
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
                if (job.ProcessedCount == processedBefore)
                {
                    logger.LogWarning(ex,
                        "Failed to process enrichment result for title '{TitleName}' (ID: {TitleId})",
                        title.Name, title.Id);
                    job.RecordFailed(title.Id, title.Name, ex.Message, JobErrorReason.ProcessingError, occurredAt: clock.GetUtcNow());
                }
                else
                {
                    logger.LogWarning(ex,
                        "Scoped cleanup failed after the enrichment outcome was acknowledged for title '{TitleName}' (ID: {TitleId})",
                        title.Name, title.Id);
                }
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
        List<EnrichmentCandidate> sortedTitles,
        Platform platform,
        Dictionary<int, (Title Title, HashSet<int> OriginalMediaFileIds)> pendingTitles,
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var candidate in sortedTitles)
        {
            job.SetCurrentItem(candidate.Name);

            var title = await titleAggregates.GetAggregateAsync(candidate.TitleId, ct);
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

            pendingTitles[title.Id] = (title, title.Media.Select(m => m.FileId).ToHashSet());
            yield return (title, platform);
        }
    }

    private async Task InTitleScopeAsync(BulkEnrichmentJob job, JobContext parent,
        Func<BulkEnrichmentTitleProcessor, JobContext, Task> process)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        if (services.GetService<IAuditContext>() is JobAuditContext audit)
            audit.SetActor(job.CreatedByUserId);
        var fence = JobExecutionFenceScope.Current;
        var guard = fence is not null ? services.GetRequiredService<IJobExecutionMutationGuard>() : null;
        string? deferralReason = null;
        var owned = new JobContext(parent.WorkspacePath, parent.CheckpointAsync, parent.CancellationToken,
            async token =>
            {
                await parent.EnsureExecutionOwnershipAsync(token);
                return true;
            },
            (mutation, token) => fence is { } claim
                ? guard!.ExecuteAsync(claim.JobId, claim.Token, mutation, token)
                : parent.ExecuteOwnedMutationAsync(mutation, token),
            parent.JobId, (reason, _) => deferralReason = reason,
            parent.ReportProgress);
        try
        {
            await process(services.GetRequiredService<BulkEnrichmentTitleProcessor>(), owned);
        }
        finally
        {
            if (owned.DeferredUntil is { } due)
                parent.RequestDeferral(due, deferralReason ?? "Temporary artwork failure.");
        }
    }
}
