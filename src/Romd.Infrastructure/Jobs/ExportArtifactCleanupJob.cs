using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;
using Romd.Persistence.Repositories;

namespace Romd.Infrastructure.Jobs;

public sealed class ExportArtifactCleanupJob
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    private readonly ExportJobRepository _exportJobRepo;
    private readonly IJobRepository<ExportJob> _genericJobRepo;
    private readonly ILogger<ExportArtifactCleanupJob> _logger;

    public ExportArtifactCleanupJob(
        ExportJobRepository exportJobRepo,
        IJobRepository<ExportJob> genericJobRepo,
        ILogger<ExportArtifactCleanupJob> logger)
    {
        _exportJobRepo = exportJobRepo;
        _genericJobRepo = genericJobRepo;
        _logger = logger;
    }

    public async Task ExecuteAsync(CancellationToken ct)
    {
        var staleJobs = await _exportJobRepo.GetCompletedOlderThanAsync(MaxAge, ct);

        if (staleJobs.Count == 0)
            return;

        _logger.LogInformation("Cleaning up {Count} stale export artifacts", staleJobs.Count);

        foreach (var job in staleJobs)
        {
            if (job.ExportPath is not null && File.Exists(job.ExportPath))
            {
                try
                {
                    File.Delete(job.ExportPath);
                    _logger.LogDebug("Deleted export artifact {Path}", job.ExportPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete export artifact {Path}", job.ExportPath);
                    continue;
                }
            }

            job.SetExportPath(null!);
            await _genericJobRepo.UpdateAsync(job, ct);
        }
    }
}
