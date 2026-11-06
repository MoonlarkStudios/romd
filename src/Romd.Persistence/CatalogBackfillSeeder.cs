using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Romd.Admin.Application.Catalog;
using Romd.Admin.Application.Libraries;

namespace Romd.Persistence;

/// <summary>
///     One-time, idempotent catalog backfill. Platforms ingested before the canonical catalog
///     existed have no <c>CatalogRelease</c> rows (their <c>CatalogRebuiltAt</c> is null); this
///     builds the projection for them on startup and then flags every library for
///     rematerialization, because the materialized-release key changed to <c>CatalogReleaseId</c>.
///     Runs synchronously before the materialization dispatcher so libraries never re-materialize
///     against an empty catalog. Once a platform is built its <c>CatalogRebuiltAt</c> is set, so
///     subsequent startups are no-ops.
/// </summary>
public sealed class CatalogBackfillSeeder : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<CatalogBackfillSeeder> _logger;

    public CatalogBackfillSeeder(
        IServiceProvider serviceProvider,
        ILogger<CatalogBackfillSeeder> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        await context.Database.EnsureCreatedAsync(cancellationToken);

        await BackfillAsync(
            context,
            scope.ServiceProvider.GetRequiredService<ICatalogProjectionService>(),
            scope.ServiceProvider.GetRequiredService<ILibraryRepository>(),
            _logger,
            cancellationToken);
    }

    /// <summary>
    ///     Rebuilds the catalog for every not-yet-built platform and, when at least one was built,
    ///     flags all libraries for rematerialization. Idempotent: built platforms get a
    ///     <c>CatalogRebuiltAt</c> timestamp and are skipped on the next run; a platform whose
    ///     rebuild failed stays unbuilt and is retried on the next startup.
    /// </summary>
    public static async Task BackfillAsync(
        RomdDbContext context,
        ICatalogProjectionService projection,
        ILibraryRepository libraries,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var platformIds = await context.Platforms
            .Where(platform => platform.CatalogRebuiltAt == null)
            .OrderBy(platform => platform.Id)
            .Select(platform => platform.Id)
            .ToListAsync(cancellationToken);

        if (platformIds.Count == 0)
        {
            logger.LogDebug("Catalog already backfilled for all platforms; skipping");
            return;
        }

        logger.LogInformation(
            "Backfilling canonical catalog for {Count} platform(s) before materialization (one-time)",
            platformIds.Count);

        int rebuilt = 0;
        foreach (int platformId in platformIds)
        {
            if (await projection.RebuildPlatformAsync(platformId, cancellationToken))
            {
                rebuilt++;
            }
        }

        // The materialized-release projection is keyed on CatalogReleaseId now, so every library
        // must rebuild against the freshly-built catalog. Only flag once something was actually
        // built — if every rebuild failed there is nothing to materialize against, and those
        // platforms remain unbuilt for the next startup to retry.
        if (rebuilt > 0)
        {
            await libraries.FlagAllForRematerializationAsync(cancellationToken);
        }

        logger.LogInformation(
            "Catalog backfill complete: rebuilt {Rebuilt}/{Total} platform(s); flagged libraries: {Flagged}",
            rebuilt,
            platformIds.Count,
            rebuilt > 0);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
