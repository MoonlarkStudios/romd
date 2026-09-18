using Hangfire;
using Hangfire.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Romd.Application.Common.Configuration;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Import;

namespace Romd.Infrastructure.Jobs;

/// <summary>
///     Background service that cleans up orphaned job workspaces and detects stale jobs.
///     Runs on startup and periodically to clean workspaces older than max age
///     and mark jobs as failed if they haven't reported progress.
/// </summary>
public sealed class OrphanedJobCleanupJob
{
    private static readonly TimeSpan MaxWorkspaceAge = TimeSpan.FromHours(4);
    private static readonly TimeSpan StaleJobThreshold = TimeSpan.FromMinutes(30);
    private readonly ILogger<OrphanedJobCleanupJob> _logger;

    private readonly IRomdOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly TimeProvider _timeProvider;

    public OrphanedJobCleanupJob(
        IRomdOptions options,
        IServiceProvider serviceProvider,
        TimeProvider timeProvider,
        ILogger<OrphanedJobCleanupJob> logger)
    {
        _options = options;
        _serviceProvider = serviceProvider;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    private string WorkspacesBasePath => Path.Combine(_options.DataDirectory, "temp", "jobs");

    [AutomaticRetry(Attempts = 1)]
    [Queue(JobQueues.Default)]
    public async Task ExecuteAsync(PerformContext ctx)
    {
        await FailStaleJobsAsync(ctx.CancellationToken.ShutdownToken);
        CleanupWorkspaces();
        await ProcessImportManifestsAsync(ctx.CancellationToken.ShutdownToken);
    }

    private async Task FailStaleJobsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IUploadJobRepository>();

            int failedCount = await repository.FailStaleJobsAsync(StaleJobThreshold, ct);

            if (failedCount > 0)
            {
                _logger.LogWarning(
                    "Marked {Count} stale job(s) as failed (no progress for {Minutes} minutes)",
                    failedCount,
                    StaleJobThreshold.TotalMinutes);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error detecting stale jobs");
        }
    }

    private void CleanupWorkspaces()
    {
        if (!Directory.Exists(WorkspacesBasePath))
        {
            return;
        }

        int cleanedCount = 0;
        string[] directories = Directory.GetDirectories(WorkspacesBasePath);

        foreach (string directory in directories)
        {
            try
            {
                string directoryName = Path.GetFileName(directory);
                if (!Guid.TryParse(directoryName, out _))
                {
                    // Not a valid job directory name, clean it up
                    DeleteDirectory(directory);
                    cleanedCount++;
                    continue;
                }

                // Check workspace age
                var creationTime = Directory.GetCreationTimeUtc(directory);
                var age = _timeProvider.GetUtcNow().UtcDateTime - creationTime;

                if (age > MaxWorkspaceAge)
                {
                    _logger.LogDebug("Cleaning workspace {Directory} ({Hours:F1} hours old)",
                        directoryName, age.TotalHours);
                    DeleteDirectory(directory);
                    cleanedCount++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up workspace directory {Directory}", directory);
            }
        }

        if (cleanedCount > 0)
        {
            _logger.LogInformation("Cleaned up {Count} orphaned job workspace(s)", cleanedCount);
        }
    }

    /// <summary>
    ///     Durable backstop for move-mode import manifests ("{jobId:N}.import.json" siblings of
    ///     the job workspaces), driven by the PERSISTED job row: a Completed job re-runs the
    ///     idempotent source cleanup (retryable across sweeps); any other terminal job has its
    ///     manifest deleted while every source file is retained; a non-terminal or unresolvable
    ///     job leaves the manifest until the age-out, which warns before discarding a move
    ///     manifest because its retained sources then stay behind permanently.
    /// </summary>
    private async Task ProcessImportManifestsAsync(CancellationToken ct)
    {
        if (!Directory.Exists(WorkspacesBasePath))
        {
            return;
        }

        foreach (string manifestPath in Directory.GetFiles(WorkspacesBasePath, "*.import.json"))
        {
            try
            {
                await ProcessImportManifestAsync(manifestPath, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process import manifest {Manifest}", manifestPath);
            }
        }
    }

    private async Task ProcessImportManifestAsync(string manifestPath, CancellationToken ct)
    {
        string fileName = Path.GetFileName(manifestPath);
        UploadJob? job = null;
        if (Guid.TryParseExact(fileName[..fileName.IndexOf('.')], "N", out Guid jobId))
        {
            using var scope = _serviceProvider.CreateScope();
            job = await scope.ServiceProvider
                .GetRequiredService<IUploadJobRepository>()
                .GetByIdAsync(jobId, ct);

            if (job is { ImportMove: true, PhaseEnum: UploadPhase.Completed })
            {
                await scope.ServiceProvider
                    .GetRequiredService<ImportSourceCleanup>()
                    .RunCompletedAsync(job, ct);
                if (!File.Exists(manifestPath))
                {
                    return;
                }
            }
            else if (job is { IsTerminal: true })
            {
                _logger.LogInformation(
                    "Import job {JobId} is terminal as {Phase}; deleting its move manifest, source files retained",
                    job.Id, job.Phase);
                File.Delete(manifestPath);
                return;
            }
        }

        var age = _timeProvider.GetUtcNow().UtcDateTime - File.GetCreationTimeUtc(manifestPath);
        if (age > MaxWorkspaceAge)
        {
            _logger.LogWarning(
                "Aging out move-mode import manifest {Manifest} ({Hours:F1} hours old, job phase {Phase}); any remaining source files stay retained and must be removed manually",
                fileName, age.TotalHours, job?.Phase ?? "unknown");
            File.Delete(manifestPath);
        }
    }

    private void DeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete directory {Directory}", directory);
        }
    }
}
