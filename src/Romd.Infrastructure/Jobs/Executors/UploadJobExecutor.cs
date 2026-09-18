using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Ingestion.Classification;
using Romd.Admin.Application.Ingestion.Extraction;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Domain.Identity;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Jobs.Processors;
using Romd.Persistence.Identity;

namespace Romd.Infrastructure.Jobs.Executors;

public sealed class UploadJobExecutor : IJobExecutor<UploadJob>
{
    private const int _progressSaveInterval = 50;

    private readonly IFileClassifier _classifier;
    private readonly DatProcessor _datProcessor;
    private readonly IEnrichmentScheduler _enrichmentScheduler;
    private readonly ArchiveExtractionStep _extraction;
    private readonly ILogger<UploadJobExecutor> _logger;
    private readonly IEnumerable<IMetadataProvider> _metadataProviders;
    private readonly RomProcessor _romProcessor;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly UserManager<RomdUser> _userManager;

    public UploadJobExecutor(
        ArchiveExtractionStep extraction,
        IFileClassifier classifier,
        DatProcessor datProcessor,
        RomProcessor romProcessor,
        UserManager<RomdUser> userManager,
        IEnrichmentScheduler enrichmentScheduler,
        IEnumerable<IMetadataProvider> metadataProviders,
        IServiceScopeFactory scopeFactory,
        ILogger<UploadJobExecutor> logger)
    {
        _extraction = extraction;
        _classifier = classifier;
        _datProcessor = datProcessor;
        _romProcessor = romProcessor;
        _userManager = userManager;
        _enrichmentScheduler = enrichmentScheduler;
        _metadataProviders = metadataProviders;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task ExecuteAsync(UploadJob job, JobContext context)
    {
        var ct = context.CancellationToken;
        var workDirectory = context.WorkspacePath
            ?? throw new InvalidOperationException("Upload job requires a workspace");

        // Phase 1: Extract nested archives
        _logger.LogInformation("Job {Id}: Starting extraction", job.Id);
        using var extractionGate = new SemaphoreSlim(1);
        await WithHeartbeatAsync(async token =>
        {
            await _extraction.ExtractRecursivelyAsync(workDirectory, token, async (path, updateToken) =>
            {
                await extractionGate.WaitAsync(updateToken);
                try { job.SetCurrentItem(path); await context.CheckpointAsync(updateToken); }
                finally { extractionGate.Release(); }
            });
        }, async token =>
        {
            await extractionGate.WaitAsync(token);
            try { await context.CheckpointAsync(token); }
            finally { extractionGate.Release(); }
        }, ct);

        // Phase 2: Classify all files
        job.BeginClassification();
        await context.CheckpointAsync(ct);

        var (dats, roms) = await ClassifyFilesAsync(workDirectory, job, context, ct);
        job.SetDiscoveredFiles(dats.Count, roms.Count);
        await context.CheckpointAsync(ct);

        _logger.LogInformation(
            "Job {Id}: Discovered {Dats} DATs, {Roms} ROMs",
            job.Id, dats.Count, roms.Count);

        // Honor the per-upload "Allow Unidentified" choice, but only for users permitted to keep
        // unmatched ROMs. When false (or the user lacks permission), unmatched ROMs are rejected.
        bool allowUnidentified = job.AllowUnidentified
            && await CanUserUploadUnidentifiedRomsAsync(job.CreatedByUserId);
        _logger.LogInformation(
            "Job {Id}: CreatedByUserId={UserId}, Requested={Requested}, AllowUnidentified={AllowUnidentified}",
            job.Id, job.CreatedByUserId, job.AllowUnidentified, allowUnidentified);

        // Per-file provenance buffer, flushed on the checkpoint cadence + once per phase.
        var sink = new JobItemSink(_scopeFactory);

        // Phase 3: Process DATs sequentially
        HashSet<int> matchedPlatformIds;
        try
        {
            await ProcessDatsAsync(job, dats, workDirectory, context, sink);
            await sink.FlushAsync(ct);
            // Phase 4: Process ROMs in parallel
            matchedPlatformIds = await ProcessRomsAsync(
            job, roms, workDirectory, job.MaxParallelRoms, allowUnidentified, context, sink);
        }
        finally
        {
            // Keep successfully completed outcomes even if a later item is cancelled or fails.
            await sink.FlushAsync(CancellationToken.None);
        }

        _logger.LogInformation(
            "Job {Id} complete: {Dats} DATs, {Roms} ROMs ingested, {Dupes} dupes, {Rejected} rejected, {Errors} errors",
            job.Id, job.DatsSucceeded, job.RomsIngested,
            job.RomsDeduplicated, job.RomsRejected, job.Errors.Count);

        // Auto-enrich: one bulk job per platform this import touched, deferred until ingest is done
        // so it never contends with the import. The bulk executor processes only titles needing
        // enrichment, so re-importing or already-enriched platforms cost almost nothing.
        var enrichmentPlatformIds = new HashSet<int>(matchedPlatformIds);
        if (job.DatsSucceeded > 0 && job.PlatformId.HasValue)
        {
            enrichmentPlatformIds.Add(job.PlatformId.Value);
        }

        foreach (int platformId in enrichmentPlatformIds)
        {
            await TryEnqueueAutoEnrichmentAsync(platformId, job.CreatedByUserId);
        }

    }

    private async Task TryEnqueueAutoEnrichmentAsync(int platformId, Guid? createdByUserId)
    {
        try
        {
            foreach (var provider in _metadataProviders)
            {
                await provider.InitializeAsync();
            }
            var hasConfiguredProvider = _metadataProviders.Any(p => p.IsConfigured);
            if (!hasConfiguredProvider)
            {
                _logger.LogDebug("Skipping auto-enrichment: no configured metadata providers");
                return;
            }

            await _enrichmentScheduler.EnqueueBulkAsync(platformId, createdByUserId: createdByUserId);
        }
        catch (Exception ex)
        {
            // Auto-enrichment failure should not fail the upload job
            _logger.LogWarning(ex, "Failed to enqueue auto-enrichment for platform {PlatformId}", platformId);
        }
    }

    private async Task ProcessDatsAsync(
        UploadJob job,
        List<string> dats,
        string workDirectory,
        JobContext context,
        JobItemSink sink)
    {
        var ct = context.CancellationToken;
        job.BeginDatIngestion();
        await context.CheckpointAsync(ct);

        foreach (string datPath in dats.OrderBy(Path.GetFileName))
        {
            ct.ThrowIfCancellationRequested();

            job.SetCurrentItem(Path.GetFileName(datPath));
            await context.CheckpointAsync(ct);
            DatProcessingResult result = default;
            await WithHeartbeatAsync(async token =>
            {
                result = await _datProcessor.ProcessAsync(datPath, job.PlatformId, token);
            }, context.CheckpointAsync, ct);

            job.RecordDatResult(result.Success, result.Error);
            // Provenance keys on the workspace-relative path so move-mode cleanup can join a
            // JobItem back to its manifest entry exactly (leaf names can collide across folders).
            sink.Add(JobItem.ForDat(
                job.Id, Path.GetRelativePath(workDirectory, datPath), result.SizeBytes,
                result.Success, result.Routed,
                result.PlatformId, result.DatFileId, result.GameCount, result.Error));

            await context.CheckpointAsync(ct);
        }
    }

    private async Task<HashSet<int>> ProcessRomsAsync(
        UploadJob job,
        List<string> roms,
        string workDirectory,
        int maxParallelRoms,
        bool allowUnidentified,
        JobContext context,
        JobItemSink sink)
    {
        var ct = context.CancellationToken;
        job.BeginRomIngestion();
        await context.CheckpointAsync(ct);

        using var semaphore = new SemaphoreSlim(maxParallelRoms);
        int processedCount = 0;
        using var progressGate = new SemaphoreSlim(1);
        var sinceSave = Stopwatch.StartNew();
        var matchedPlatformIds = new HashSet<int>();

        await WithHeartbeatAsync(token => Parallel.ForEachAsync(roms, token, async (romPath, innerCt) =>
        {
            await semaphore.WaitAsync(innerCt);
            try
            {
                await progressGate.WaitAsync(innerCt);
                try { job.SetCurrentItem(Path.GetRelativePath(workDirectory, romPath)); }
                finally { progressGate.Release(); }
                var result = await _romProcessor.ProcessAsync(
                    romPath, allowUnidentified, job.ArchiveOnly, innerCt);

                await progressGate.WaitAsync(CancellationToken.None);
                try
                {
                    job.SetCurrentItem(Path.GetRelativePath(workDirectory, romPath));
                    job.RecordRomResult(result.Outcome, result.Error);
                    if (result.PlatformId is int platformId && result.MatchedTitleIds is { Count: > 0 })
                    {
                        matchedPlatformIds.Add(platformId);
                    }
                    processedCount++;

                    // Provenance keys on the workspace-relative path so move-mode cleanup can join a
                    // JobItem back to its manifest entry exactly (leaf names can collide across folders).
                    sink.Add(JobItem.ForRom(
                        job.Id, Path.GetRelativePath(workDirectory, romPath), result.SizeBytes, result.Outcome,
                        result.RomFileId, result.MatchedTitleIds, result.PlatformId, result.Error,
                        job.ArchiveOnly));

                    if (processedCount % _progressSaveInterval == 0 || sinceSave.Elapsed >= TimeSpan.FromSeconds(1))
                    {
                        await sink.FlushAsync(CancellationToken.None);
                        await context.CheckpointAsync(CancellationToken.None);
                        sinceSave.Restart();
                    }
                }
                finally { progressGate.Release(); }
            }
            finally
            {
                semaphore.Release();
            }
        }), async token =>
        {
            await progressGate.WaitAsync(token);
            try
            {
                await sink.FlushAsync(CancellationToken.None);
                await context.CheckpointAsync(CancellationToken.None);
            }
            finally { progressGate.Release(); }
        }, ct);

        return matchedPlatformIds;
    }

    internal static async Task WithHeartbeatAsync(
        Func<CancellationToken, Task> work,
        Func<CancellationToken, Task> checkpoint,
        CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task ReportAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            try
            {
                while (await timer.WaitForNextTickAsync(lifetime.Token))
                    await checkpoint(lifetime.Token);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch
            {
                await lifetime.CancelAsync();
                throw;
            }
        }

        var heartbeat = ReportAsync();
        try { await work(lifetime.Token); }
        finally
        {
            await lifetime.CancelAsync();
            await heartbeat;
        }
    }

    private async Task<(List<string> Dats, List<string> Roms)> ClassifyFilesAsync(
        string directory,
        UploadJob job,
        JobContext context,
        CancellationToken ct)
    {
        var dats = new List<string>();
        var roms = new List<string>();
        var sinceSave = Stopwatch.StartNew();

        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            await using var stream = File.OpenRead(file);
            var classification = await _classifier.ClassifyAsync(stream, Path.GetFileName(file), ct);

            switch (classification.Type)
            {
                case FileType.Dat:
                    dats.Add(file);
                    break;
                case FileType.Rom:
                    roms.Add(file);
                    break;
            }
            if (sinceSave.Elapsed >= TimeSpan.FromSeconds(1))
            {
                job.SetCurrentItem(Path.GetRelativePath(directory, file));
                job.SetDiscoveredFiles(dats.Count, roms.Count);
                await context.CheckpointAsync(ct);
                sinceSave.Restart();
            }
        }

        return (dats, roms);
    }

    private async Task<bool> CanUserUploadUnidentifiedRomsAsync(Guid? userId)
    {
        if (userId is null)
        {
            return false;
        }

        var user = await _userManager.FindByIdAsync(userId.Value.ToString());
        if (user is null)
        {
            return false;
        }

        var roles = await _userManager.GetRolesAsync(user);

        if (roles.Contains(RomdRoleType.Manager.ToString()) ||
            roles.Contains(RomdRoleType.Admin.ToString()))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Thread-safe buffer for per-file provenance. Items are enqueued from the parallel ROM loop
    ///     and flushed in batches; each flush uses its own DI scope (own DbContext) so concurrent
    ///     flushes never share an EF context.
    /// </summary>
    private sealed class JobItemSink
    {
        private readonly ConcurrentQueue<JobItem> _pending = new();
        private readonly IServiceScopeFactory _scopeFactory;

        public JobItemSink(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

        public void Add(JobItem item) => _pending.Enqueue(item);

        public async Task FlushAsync(CancellationToken ct)
        {
            var batch = new List<JobItem>();
            while (_pending.TryDequeue(out var item))
            {
                batch.Add(item);
            }

            if (batch.Count == 0)
            {
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IJobItemRepository>();
            await repository.AddRangeAsync(batch, ct);
        }
    }
}
