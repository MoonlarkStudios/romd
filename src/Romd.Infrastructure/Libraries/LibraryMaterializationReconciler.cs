using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Libraries;
using Romd.Domain.Jobs;

namespace Romd.Infrastructure.Libraries;

/// <summary>
///     Converts catalog/library invalidation flags into durable application jobs after the
///     catalog is clean. Job delivery and abandoned-execution recovery belong to JobDispatchWorker.
/// </summary>
public sealed class LibraryMaterializationReconciler(
    IServiceScopeFactory scopeFactory,
    ILogger<LibraryMaterializationReconciler> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await DispatchPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error dispatching materialization jobs");
            }
        }
    }

    private async Task DispatchPendingAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var libraryRepo = scope.ServiceProvider.GetRequiredService<ILibraryRepository>();
        var jobRepo = scope.ServiceProvider.GetRequiredService<IMaterializationJobRepository>();

        var activeJobs = await jobRepo.GetActiveAsync(ct);
        var libraries = await libraryRepo.GetNeedingMaterializationAsync(ct);
        if (libraries.Count == 0) return;

        var catalogProjection = scope.ServiceProvider.GetRequiredService<ICatalogProjectionService>();
        var platformIdsNeedingRebuild = await catalogProjection.GetPlatformIdsNeedingRebuildAsync(ct);

        var librariesWithActiveJobs = activeJobs
            .Select(job => job.LibraryId)
            .ToHashSet();

        foreach (var library in libraries)
        {
            if (librariesWithActiveJobs.Contains(library.Id))
            {
                logger.LogDebug(
                    "Skipping materialization for library '{Name}' — job already active",
                    library.Name);
                continue;
            }

            if (MaterializationCatalogGate.IsBlocked(library.Configuration, platformIdsNeedingRebuild))
            {
                logger.LogInformation(
                    "Skipping materialization for library '{Name}' — catalog projection rebuild pending",
                    library.Name);
                continue;
            }

            var job = MaterializationJob.Create(library.Id, library.Name);
            if (!await jobRepo.TryAddIfNoActiveForLibraryAsync(job, ct))
            {
                logger.LogDebug(
                    "Skipping materialization for library '{Name}' — job already active",
                    library.Name);
                continue;
            }

            logger.LogInformation(
                "Enqueued materialization job {JobId} for library '{Name}'",
                job.Id, library.Name);
        }
    }
}
