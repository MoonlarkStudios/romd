using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Libraries;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Diagnostics;

namespace Romd.Infrastructure.Jobs;

/// <summary>
///     Recurring convergence sweep for the DAT replacement saga. Runs three idempotent
///     duties in order, each in its own scope and failure-isolated so one failing duty
///     never blocks the others:
///     1. terminally fail stale non-terminal replace jobs (no checkpoint progress within
///        the stale threshold), preserving their recorded attempt errors;
///     2. age out abandoned PendingActivation versions no live job can still activate,
///        converging to the design's repairable state: old version stays Active, the new
///        one is discarded, the job is Failed with its errors. Deleting the version row
///        releases its stored-file reference; the daily orphaned-file cleanup owns the
///        bytes — the sweep never deletes blobs;
///     3. re-run superseded retention (N=1 newest per source) to cover a crash between
///        activation and the replace job's own cleanup phase;
///     4. orphaned-title backstop: apply the retain-as-UserOnly-or-delete policy to titles
///        that lost their last source link outside a policy-applying command
///        (docs/decisions/neutral-source-identity.md). A creation-age grace window protects
///        titles mid-derivation between creation and linking.
/// </summary>
public sealed class DatReplacementConvergenceSweepJob
{
    private static readonly TimeSpan StaleJobThreshold = OperationalDiagnosticsPolicy.ReplaceDatStalledAfter;

    // Comfortably larger than the 30-minute stale-job threshold, so duty 1 has terminally
    // failed any job that could still adopt a pending version long before that version
    // becomes age-eligible, and larger than the 4-hour job-workspace retention, so no
    // redelivered ingest could resume anyway. The cost of waiting is bounded: the partial
    // unique index allows at most one PendingActivation version per source.
    private static readonly TimeSpan AbandonedPendingVersionAge = TimeSpan.FromHours(24);

    // Titles are created before their links inside a derivation batch; one hour comfortably
    // outlasts any ingest, so the backstop never races an in-flight derivation.
    private static readonly TimeSpan OrphanedTitleGrace = TimeSpan.FromHours(1);
    private const int OrphanedTitleBatchLimit = 200;

    private readonly ILogger<DatReplacementConvergenceSweepJob> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;

    public DatReplacementConvergenceSweepJob(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<DatReplacementConvergenceSweepJob> logger)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 1)]
    [Queue(JobQueues.Default)]
    public async Task ExecuteAsync(CancellationToken ct)
    {
        await RunDutyAsync("fail stale replace jobs", FailStaleReplaceJobsAsync, ct);
        await RunDutyAsync("age out abandoned pending versions", AgeOutAbandonedPendingVersionsAsync, ct);
        await RunDutyAsync("enforce superseded retention", EnforceSupersededRetentionAsync, ct);
        await RunDutyAsync("orphaned-title backstop", ApplyOrphanedTitleBackstopAsync, ct);
    }

    private async Task RunDutyAsync(
        string duty,
        Func<IServiceProvider, CancellationToken, Task> execute,
        CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            await execute(scope.ServiceProvider, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DAT replacement convergence sweep duty '{Duty}' failed", duty);
        }
    }

    private async Task FailStaleReplaceJobsAsync(IServiceProvider services, CancellationToken ct)
    {
        var repository = services.GetRequiredService<IReplaceDatJobRepository>();
        var staleJobs = await repository.GetStaleStartedAsync(StaleJobThreshold, ct);

        foreach (var job in staleJobs)
        {
            // Fail appends the timeout reason while preserving the recorded attempt errors.
            job.Fail("Job timed out (no progress updates received)");
            await repository.UpdateAsync(job, ct);
            _logger.LogWarning(
                "Failed stale replace DAT job {JobId} (no progress for {Minutes} minutes)",
                job.Id,
                StaleJobThreshold.TotalMinutes);
        }
    }

    private async Task AgeOutAbandonedPendingVersionsAsync(IServiceProvider services, CancellationToken ct)
    {
        var repository = services.GetRequiredService<IDatRepository>();
        var unitOfWork = services.GetRequiredService<IUnitOfWork>();
        var projection = services.GetRequiredService<ICatalogProjectionService>();
        var materialization = services.GetRequiredService<ILibraryMaterializationService>();
        var cutoff = _timeProvider.GetUtcNow() - AbandonedPendingVersionAge;

        await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
        int deleted;
        try
        {
            var affectedPlatformIds =
                await repository.GetRoutedPlatformIdsWithAgedPendingVersionsAsync(cutoff, ct);
            deleted = await repository.DeleteAbandonedPendingVersionsAsync(cutoff, ct);
            if (deleted > 0)
            {
                foreach (int platformId in affectedPlatformIds)
                {
                    await projection.RefreshPlatformPayloadAsync(platformId, ct);
                }
                await MarkCatalogAndLibrariesDirtyAsync(
                    affectedPlatformIds, projection, materialization, ct);
            }

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }

        if (deleted > 0)
        {
            _logger.LogInformation(
                "Aged out {Count} abandoned pending DAT version(s) older than {Hours} hours",
                deleted,
                AbandonedPendingVersionAge.TotalHours);
        }
    }

    private async Task EnforceSupersededRetentionAsync(IServiceProvider services, CancellationToken ct)
    {
        var repository = services.GetRequiredService<IDatRepository>();
        var retention = services.GetRequiredService<IDatVersionRetentionCoordinator>();
        var sourceIds = await repository.GetSourceIdsExceedingSupersededRetentionAsync(ct);

        foreach (int sourceId in sourceIds)
        {
            int deleted = await retention.EnforceAsync(sourceId, ct);

            if (deleted > 0)
            {
                _logger.LogInformation(
                    "Retention backstop removed {Count} older superseded version(s) of source {SourceId}",
                    deleted,
                    sourceId);
            }
        }
    }

    private static async Task MarkCatalogAndLibrariesDirtyAsync(
        IReadOnlyList<int> platformIds,
        ICatalogProjectionService projection,
        ILibraryMaterializationService materialization,
        CancellationToken ct)
    {
        foreach (int platformId in platformIds)
        {
            await projection.MarkPlatformDirtyAsync(platformId, ct);
            await materialization.FlagAffectedLibrariesAsync(platformId, ct);
        }
    }
    private async Task ApplyOrphanedTitleBackstopAsync(IServiceProvider services, CancellationToken ct)
    {
        var titleRepository = services.GetRequiredService<Romd.Admin.Application.Titles.ITitleRepository>();
        var unitOfWork = services.GetRequiredService<Romd.Admin.Application.Common.Persistence.IUnitOfWork>();

        var cutoff = _timeProvider.GetUtcNow() - OrphanedTitleGrace;
        var orphanedTitleIds = await titleRepository.GetOrphanedTitleIdsAsync(
            cutoff, OrphanedTitleBatchLimit, ct);

        if (orphanedTitleIds.Count == 0)
        {
            return;
        }

        int deleted = 0;
        int retained = 0;
        foreach (int titleId in orphanedTitleIds)
        {
            // One transaction per title: the release+title deletion is atomic against a
            // concurrent re-link (callers own transactions; the repository only guards
            // per statement), and one failing title never rolls back the batch.
            await using var transaction = await unitOfWork.BeginTransactionAsync(ct);
            bool wasDeleted = await titleRepository.DeleteOrRetainAsync(titleId, ct);
            await transaction.CommitAsync(ct);

            if (wasDeleted)
            {
                deleted++;
            }
            else
            {
                retained++;
            }
        }

        _logger.LogInformation(
            "Orphaned-title backstop: {Deleted} deleted, {Retained} retained as UserOnly",
            deleted, retained);
    }

}
