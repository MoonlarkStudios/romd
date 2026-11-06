using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Libraries;

namespace Romd.Infrastructure.Source;

/// <summary>
///     Background service that polls for platforms whose catalog projection is Dirty or
///     Failed and rebuilds each one sequentially. Recovery is at-least-once and
///     duplicate-safe: a rebuild is a full idempotent pass whose Clean completion state
///     commits atomically with the rebuilt rows, so a crashed or failed pass is simply
///     retried on the next tick. After a successful rebuild the affected libraries are
///     flagged for rematerialization (idempotent), which the materialization dispatcher
///     picks up once the platform is Clean.
/// </summary>
public sealed class CatalogProjectionRecoveryDispatcher(
    IServiceScopeFactory scopeFactory,
    ILogger<CatalogProjectionRecoveryDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RecoverPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error recovering catalog projections");
            }
        }
    }

    private async Task RecoverPendingAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var catalogProjection = scope.ServiceProvider.GetRequiredService<ICatalogProjectionService>();
        var libraryRepo = scope.ServiceProvider.GetRequiredService<ILibraryRepository>();

        var platformIds = await catalogProjection.GetPlatformIdsNeedingRebuildAsync(ct);
        foreach (int platformId in platformIds)
        {
            ct.ThrowIfCancellationRequested();

            bool rebuilt = await catalogProjection.RebuildPlatformAsync(platformId, ct);
            if (!rebuilt)
            {
                logger.LogWarning(
                    "Catalog projection recovery failed for platform {PlatformId}; retrying on the next pass",
                    platformId);
                continue;
            }

            await libraryRepo.FlagForRematerializationByPlatformAsync(platformId, ct);
            logger.LogInformation(
                "Recovered catalog projection for platform {PlatformId}",
                platformId);
        }
    }
}
