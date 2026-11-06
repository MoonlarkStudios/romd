using Romd.Admin.Application.Titles.Commands.QueueBulkEnrichmentJob;
using Romd.Application.Common.Cqrs;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Romd.Application.Common.Content;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Ingestion.Classification;
using Romd.Admin.Application.Ingestion.Extraction;
using Romd.Admin.Application.Ingestion.Import;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Titles.Enrichment;
using Romd.Admin.Application.Titles;
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
    private readonly ICommandHandler<QueueBulkEnrichmentJobCommand, Guid> _enrichmentAcceptance;
    private readonly ArchiveExtractionStep _extraction;
    private readonly ILogger<UploadJobExecutor> _logger;
    private readonly IEnumerable<IMetadataProvider> _metadataProviders;
    private readonly RomProcessor _romProcessor;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly UserManager<RomdUser> _userManager;
    private readonly TimeProvider _clock;

    public UploadJobExecutor(
        ArchiveExtractionStep extraction,
        IFileClassifier classifier,
        DatProcessor datProcessor,
        RomProcessor romProcessor,
        UserManager<RomdUser> userManager,
        ICommandHandler<QueueBulkEnrichmentJobCommand, Guid> enrichmentAcceptance,
        IEnumerable<IMetadataProvider> metadataProviders,
        IServiceScopeFactory scopeFactory,
        ILogger<UploadJobExecutor> logger,
        TimeProvider? clock = null)
    {
        _extraction = extraction;
        _classifier = classifier;
        _datProcessor = datProcessor;
        _romProcessor = romProcessor;
        _userManager = userManager;
        _enrichmentAcceptance = enrichmentAcceptance;
        _metadataProviders = metadataProviders;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task ExecuteAsync(UploadJob job, JobContext context)
    {
        var ct = context.CancellationToken;
        var workDirectory = context.WorkspacePath
            ?? throw new InvalidOperationException("Upload job requires a workspace");

        if (job.TrackedOnly)
        {
            using var scope = _scopeFactory.CreateScope();
            if (!await scope.ServiceProvider.GetRequiredService<ITrackedTitleReader>().HasTrackedAsync(cancellationToken: ct))
                throw new InvalidOperationException("Track at least one title before importing tracked-title ROMs.");
        }

        // Re-entry dispatches on the durable phase. Extraction has its own worker-owned
        // checkpoint; ingestion never re-extracts files that completed imports reference.
        if (job.PhaseEnum == UploadPhase.Extracting)
        {
            _logger.LogInformation("Job {Id}: Starting extraction", job.Id);
            using var extractionGate = new SemaphoreSlim(1);
            async Task CheckpointExtractionAsync(CancellationToken token)
            {
                try
                { await context.CheckpointAsync(token); }
                catch (Exception ex) when (ex is not (OperationCanceledException or JobExecutionOwnershipLostException))
                {
                    // A failed DB/notification acknowledgement cannot discard the accepted
                    // inputs or a completed filesystem checkpoint. Let the runner redeliver.
                    context.RequestDeferral(_clock.GetUtcNow().Add(JobContext.UploadRetryDelay(job.DeferralCount)), ex.Message);
                    throw;
                }
            }
            try
            {
                await WithHeartbeatAsync(async token =>
                {
                    await _extraction.ExtractRecursivelyAsync(workDirectory, token, async (path, updateToken) =>
                    {
                        await extractionGate.WaitAsync(updateToken);
                        try
                        { job.SetCurrentItem(path); await CheckpointExtractionAsync(updateToken); }
                        finally { extractionGate.Release(); }
                    });
                }, async token =>
                {
                    await extractionGate.WaitAsync(token);
                    try
                    { await CheckpointExtractionAsync(token); }
                    finally { extractionGate.Release(); }
                }, ct);
            }
            catch (InvalidDataException ex)
            {
                throw new JobPermanentFailureException(ex.Message);
            }
            catch (IOException ex) when (ex is not PathTooLongException)
            {
                ct.ThrowIfCancellationRequested();
                context.RequestDeferral(_clock.GetUtcNow().Add(JobContext.UploadRetryDelay(job.DeferralCount)), ex.Message);
                _logger.LogWarning(ex, "Job {Id} deferred after extraction I/O failed; source and checkpoint retained", job.Id);
                return;
            }
            job.BeginClassification();
            await CheckpointExtractionAsync(ct);
        }

        var (dats, roms, unsupportedArchives) = await ClassifyFilesAsync(workDirectory, job, context, ct);
        if (job.PhaseEnum == UploadPhase.Classifying)
        {
            job.SetDiscoveredFiles(dats.Count, roms.Count);
            await context.CheckpointAsync(ct);
        }
        else if (job.DatsDiscovered != dats.Count || job.RomsDiscovered != roms.Count)
        {
            throw new JobPermanentFailureException("The upload workspace changed after classification; ingestion cannot safely resume.");
        }

        IReadOnlyList<JobItem> outcomes;
        try
        {
            using var recoveryScope = _scopeFactory.CreateScope();
            outcomes = await recoveryScope.ServiceProvider.GetRequiredService<IJobItemRepository>()
                .GetOutcomesAsync(job.Id, ct);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or JobExecutionOwnershipLostException))
        {
            // An unavailable journal cannot establish which members remain unfinished.
            // Preserve inputs and let the durable handoff/retry protocol recover the read.
            context.RequestDeferral(_clock.GetUtcNow().Add(JobContext.UploadRetryDelay(job.DeferralCount)), ex.Message);
            _logger.LogWarning(ex, "Job {Id} deferred because its outcome journal could not be read", job.Id);
            return;
        }
        if (outcomes.Count > job.DatsProcessed + job.RomsProcessed)
            context.ReportUploadProgress(); // Recover a committed outcome whose aggregate checkpoint was lost.
        if (job.PhaseEnum is UploadPhase.IngestingDats or UploadPhase.IngestingRoms)
            job.RestoreIngestionProgress(outcomes);
        var completed = outcomes.Select(item => (item.Kind, item.FileName)).ToHashSet();
        var discovered = dats.Select(path => (JobItemKind.Dat, Path.GetRelativePath(workDirectory, path)))
            .Concat(roms.Select(path => (JobItemKind.Rom, Path.GetRelativePath(workDirectory, path)))).ToHashSet();
        if (!completed.IsSubsetOf(discovered))
            throw new JobPermanentFailureException("The upload workspace no longer contains a recorded file.");
        dats.RemoveAll(path => completed.Contains((JobItemKind.Dat, Path.GetRelativePath(workDirectory, path))));
        roms.RemoveAll(path => completed.Contains((JobItemKind.Rom, Path.GetRelativePath(workDirectory, path))));
        if (job.PhaseEnum == UploadPhase.IngestingRoms && dats.Count > 0)
            throw new JobPermanentFailureException("The completed DAT phase has missing file outcomes.");
        var matchedPlatformIds = outcomes
            .Where(item => item.Kind == JobItemKind.Rom && item.PlatformId.HasValue && item.MatchedTitleIds.Count > 0)
            .Select(item => item.PlatformId!.Value).ToHashSet();

        // Honor the per-upload "Allow Unidentified" choice, but only for users permitted to keep
        // unmatched ROMs. When false (or the user lacks permission), unmatched ROMs are rejected.
        bool allowUnidentified = job.AllowUnidentified
            && await CanUserUploadUnidentifiedRomsAsync(job.CreatedByUserId);
        _logger.LogInformation(
            "Job {Id}: CreatedByUserId={UserId}, Requested={Requested}, AllowUnidentified={AllowUnidentified}",
            job.Id, job.CreatedByUserId, job.AllowUnidentified, allowUnidentified);

        // Per-file provenance buffer, flushed on the checkpoint cadence + once per phase.
        var sink = new JobItemSink(_scopeFactory, context.ReportUploadProgress);

        // Phase 3: Process DATs sequentially
        Exception? workFailure = null;
        try
        {
            try
            {
                if (job.PhaseEnum is UploadPhase.Classifying or UploadPhase.IngestingDats)
                {
                    await ProcessDatsAsync(job, dats, workDirectory, context, sink);
                    await sink.FlushAsync(ct);
                }
                matchedPlatformIds.UnionWith(await ProcessRomsAsync(
                    job, roms, workDirectory, job.MaxParallelRoms, allowUnidentified, context, sink, unsupportedArchives));
            }
            catch (Exception ex) when (IngestionFailure.RequiresDeferral(ex))
            {
                workFailure = ex;
                // All parallel work has settled before handoff. Persist completed siblings first;
                // redelivery consults the journal before retrying any pressured or uncertain item.
                ct.ThrowIfCancellationRequested();
                // Record retry intent before flushing, so a failed journal acknowledgement also
                // preserves the workspace. The final flush chooses the delay from durable progress.
                context.RequestDeferral(_clock.GetUtcNow().Add(JobContext.UploadRetryDelay(job.DeferralCount)), ex.Message);
                _logger.LogInformation("Job {Id} deferred for retry: {Reason}", job.Id, ex.Message);
                return;
            }
        }
        catch (Exception error)
        {
            workFailure = error;
            throw;
        }
        finally
        {
            // Keep successfully completed outcomes even if a later item is cancelled or fails.
            try
            {
                await sink.FlushAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                if (IngestionFailure.RequiresDeferral(ex))
                    // A final journal failure must preserve the same retry handoff as an earlier one.
                    context.RequestDeferral(_clock.GetUtcNow().Add(JobContext.UploadRetryDelay(job.DeferralCount)), ex.Message);
                if (workFailure is not null)
                {
                    _logger.LogWarning(ex, "Final outcome flush also failed for upload job {Id}; preserving its original failure", job.Id);
                    ExceptionDispatchInfo.Capture(workFailure).Throw();
                }
                throw;
            }
            context.RefreshUploadRetry(_clock.GetUtcNow(), job.DeferralCount);
        }

        if (context.DeferredUntil is not null)
            return;
        // All sibling outcomes committed. Association candidates no longer depend on enumeration order.
        using (var descriptorScope = _scopeFactory.CreateScope())
        {
            var intake = descriptorScope.ServiceProvider.GetService<Romd.Admin.Application.Catalog.IOriginalDiscDescriptorIntake>();
            if (intake is not null)
                await intake.ReconcileAsync(job.Id, ct);
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

            var result = await _enrichmentAcceptance.HandleAsync(new(platformId, CreatedByUserId: createdByUserId));
            if (result.IsError)
                _logger.LogWarning("Failed to accept auto-enrichment for platform {PlatformId}: {ErrorCodes}", platformId,
                    string.Join(", ", result.Errors.Select(error => error.Code)));
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
        if (job.PhaseEnum == UploadPhase.Classifying)
        {
            job.BeginDatIngestion();
            await context.CheckpointAsync(ct);
        }

        foreach (string datPath in dats.OrderBy(Path.GetFileName))
        {
            ct.ThrowIfCancellationRequested();

            job.SetCurrentItem(Path.GetFileName(datPath));
            await context.CheckpointAsync(ct);
            DatProcessingResult result = default;
            await WithHeartbeatAsync(async token =>
            {
                result = await _datProcessor.ProcessAsync(datPath, job.PlatformId, token,
                    new UploadItemContext(job.Id, Path.GetRelativePath(workDirectory, datPath)));
            }, context.CheckpointAsync, ct);

            var occurredAt = result.JournaledOutcome?.CreatedAt ?? _clock.GetUtcNow();
            job.RecordDatResult(result.Success, occurredAt, result.Error);
            // Provenance keys on the workspace-relative path so move-mode cleanup can join a
            // JobItem back to its manifest entry exactly (leaf names can collide across folders).
            sink.Add(result.JournaledOutcome ?? JobItem.ForDat(
                job.Id, Path.GetRelativePath(workDirectory, datPath), result.SizeBytes,
                result.Success, result.Routed,
                result.PlatformId, result.DatFileId, result.GameCount, result.Error, createdAt: occurredAt));

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
        JobItemSink sink,
        HashSet<string> unsupportedArchives)
    {
        var ct = context.CancellationToken;
        if (job.PhaseEnum == UploadPhase.IngestingDats)
        {
            job.BeginRomIngestion();
            await context.CheckpointAsync(ct);
        }

        using var semaphore = new SemaphoreSlim(maxParallelRoms);
        int processedCount = 0;
        using var progressGate = new SemaphoreSlim(1);
        var sinceSave = Stopwatch.StartNew();
        var matchedPlatformIds = new HashSet<int>();
        Exception? retryFailure = null;
        string? publicationRetry = null;

        await WithHeartbeatAsync(token => Parallel.ForEachAsync(roms, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = maxParallelRoms }, async (romPath, innerCt) =>
        {
            await semaphore.WaitAsync(innerCt);
            try
            {
                await progressGate.WaitAsync(innerCt);
                try
                { job.SetCurrentItem(Path.GetRelativePath(workDirectory, romPath)); }
                finally { progressGate.Release(); }
                var result = unsupportedArchives.Contains(romPath)
                    ? new RomProcessingResult(RomIngestOutcome.Failed,
                        ImportErrors.UnsupportedArchive(Path.GetRelativePath(workDirectory, romPath)).Description,
                        new FileInfo(romPath).Length)
                    : await _romProcessor.ProcessAsync(
                    romPath, allowUnidentified, job.ArchiveOnly, innerCt, job.TrackedOnly,
                    new UploadItemContext(job.Id, Path.GetRelativePath(workDirectory, romPath)));

                if (result.RetryReason is { } reason)
                {
                    Interlocked.CompareExchange(ref publicationRetry, reason, null);
                    return;
                }

                await progressGate.WaitAsync(CancellationToken.None);
                try
                {
                    job.SetCurrentItem(Path.GetRelativePath(workDirectory, romPath));
                    var occurredAt = result.JournaledOutcome?.CreatedAt ?? _clock.GetUtcNow();
                    job.RecordRomResult(result.Outcome, occurredAt, result.Error);
                    if (result.PlatformId is int platformId && result.MatchedTitleIds is { Count: > 0 })
                    {
                        matchedPlatformIds.Add(platformId);
                    }
                    processedCount++;

                    // Provenance keys on the workspace-relative path so move-mode cleanup can join a
                    // JobItem back to its manifest entry exactly (leaf names can collide across folders).
                    sink.Add(result.JournaledOutcome ?? JobItem.ForRom(
                        job.Id, Path.GetRelativePath(workDirectory, romPath), result.SizeBytes, result.Outcome,
                        result.RomFileId, result.MatchedTitleIds, result.PlatformId, result.Error,
                        createdAt: occurredAt, job.ArchiveOnly || job.TrackedOnly));

                    if (processedCount % _progressSaveInterval == 0 || sinceSave.Elapsed >= TimeSpan.FromSeconds(1))
                    {
                        await sink.FlushAsync(CancellationToken.None);
                        await context.CheckpointAsync(CancellationToken.None);
                        sinceSave.Restart();
                    }
                }
                finally { progressGate.Release(); }
            }
            catch (Exception ex) when (IngestionFailure.RequiresDeferral(ex))
            {
                // A queued file cannot cancel inspections already holding a slot. Continue
                // admitting siblings, journal their results, then hand off unfinished files.
                Interlocked.CompareExchange(ref retryFailure, ex, null);
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

        ct.ThrowIfCancellationRequested();
        if (retryFailure is not null)
            ExceptionDispatchInfo.Capture(retryFailure).Throw();
        if (publicationRetry is not null)
            context.RequestDeferral(_clock.GetUtcNow().Add(JobContext.UploadRetryDelay(job.DeferralCount)), publicationRetry);
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
        try
        { await work(lifetime.Token); }
        finally
        {
            await lifetime.CancelAsync();
            await heartbeat;
        }
    }

    private async Task<(List<string> Dats, List<string> Roms, HashSet<string> UnsupportedArchives)> ClassifyFilesAsync(
        string directory,
        UploadJob job,
        JobContext context,
        CancellationToken ct)
    {
        var dats = new List<string>();
        var roms = new List<string>();
        var unsupportedArchives = new HashSet<string>(StringComparer.Ordinal);
        var sinceSave = Stopwatch.StartNew();

        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            if (Path.GetRelativePath(directory, file).Length > JobItem.MaxFileNameLength)
                throw new JobPermanentFailureException("An upload relative path exceeds the 1000-character journal limit.");
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
                case FileType.Archive:
                    // Extraction already finished. A surviving archive has no supported
                    // extractor for its name; record a failed outcome without ingesting it.
                    roms.Add(file);
                    unsupportedArchives.Add(file);
                    break;
            }
            if (sinceSave.Elapsed >= TimeSpan.FromSeconds(1))
            {
                job.SetCurrentItem(Path.GetRelativePath(directory, file));
                if (job.PhaseEnum == UploadPhase.Classifying)
                    job.SetDiscoveredFiles(dats.Count, roms.Count);
                await context.CheckpointAsync(ct);
                sinceSave.Restart();
            }
        }

        dats.Sort(StringComparer.Ordinal);
        roms.Sort(StringComparer.Ordinal);
        return (dats, roms, unsupportedArchives);
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

}
