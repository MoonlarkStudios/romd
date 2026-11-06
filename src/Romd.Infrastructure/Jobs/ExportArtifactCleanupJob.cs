using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;

namespace Romd.Infrastructure.Jobs;

public sealed class ExportArtifactCleanupJob
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    private readonly IExportArtifactCleanupStore _artifacts;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _clock;
    private readonly ILogger<ExportArtifactCleanupJob> _logger;

    public ExportArtifactCleanupJob(
        IExportArtifactCleanupStore artifacts,
        IUnitOfWork unitOfWork,
        TimeProvider clock,
        ILogger<ExportArtifactCleanupJob> logger)
    {
        _artifacts = artifacts;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken ct)
    {
        var cutoff = _clock.GetUtcNow() - MaxAge;
        var staleJobs = await _artifacts.GetExpiredArtifactsAsync(cutoff, ct);

        if (staleJobs.Count == 0)
            return;

        _logger.LogInformation("Cleaning up {Count} stale export artifacts", staleJobs.Count);

        foreach (var job in staleJobs)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(job.Path))
            {
                try
                {
                    File.Delete(job.Path);
                    _logger.LogDebug("Deleted export artifact {Path}", job.Path);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete export artifact {Path}", job.Path);
                    continue;
                }
            }

            await using var transaction = await _unitOfWork.BeginTransactionAsync(ct);
            await _artifacts.ClearExpiredArtifactStagedAsync(job.JobId, job.Path, cutoff, _clock.GetUtcNow(), ct);
            await transaction.CommitAsync(ct);
        }
    }
}
