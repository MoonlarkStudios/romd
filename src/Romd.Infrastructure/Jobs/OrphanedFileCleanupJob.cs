using Hangfire;
using Hangfire.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Storage.Files;

namespace Romd.Infrastructure.Jobs;

public sealed class OrphanedFileCleanupJob
{
    private readonly ILogger<OrphanedFileCleanupJob> _logger;
    private readonly IServiceProvider _serviceProvider;

    public OrphanedFileCleanupJob(
        IServiceProvider serviceProvider,
        ILogger<OrphanedFileCleanupJob> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 1)]
    [Queue(JobQueues.Default)]
    public async Task ExecuteAsync(PerformContext ctx)
    {
        using var scope = _serviceProvider.CreateScope();
        var fileStorage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
        var ct = ctx.CancellationToken.ShutdownToken;

        var released = await scope.ServiceProvider.GetRequiredService<Romd.Application.Common.ReferenceCatalog.IReferenceCatalogService>()
            .ReleaseExpiredAssetsAsync(DateTimeOffset.UtcNow, ct);
        if (released > 0) _logger.LogInformation("Released {Count} expired reference artwork reservations", released);

        // Reclaim files (row + blob) that nothing references — e.g. left behind by bulk ROM deletes.
        // Age-guarded so an in-flight upload is never deleted.
        int reclaimed = await fileStorage.PruneUnreferencedFilesAsync(TimeSpan.FromHours(1), ct);
        if (reclaimed > 0)
        {
            _logger.LogInformation("Reclaimed {Count} unreferenced files from CAS", reclaimed);
        }

        // Then reclaim blobs on disk that have no row at all (store-then-row-commit drift).
        int pruned = await fileStorage.PruneOrphanedFilesAsync(ct);
        if (pruned > 0)
        {
            _logger.LogInformation("Pruned {Count} orphaned files from CAS", pruned);
        }
    }
}
