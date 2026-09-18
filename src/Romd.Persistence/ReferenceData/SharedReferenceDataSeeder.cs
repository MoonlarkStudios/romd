using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Romd.Domain.Source.Platform;
using Romd.Domain.Taxonomy;
using Romd.Persistence.Entities;

namespace Romd.Persistence.ReferenceData;

/// <summary>Installs bundled definitions offline. Canonical keys survive local renaming.</summary>
public sealed class SharedReferenceDataSeeder(IServiceProvider services, ILogger<SharedReferenceDataSeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RomdDbContext>();
        await InitializeAsync(db, scope.ServiceProvider.GetRequiredService<Romd.Application.Common.ReferenceCatalog.IReferenceBlobStore>(), cancellationToken);
        var unresolved = await db.PlatformAliases.CountAsync(x => x.Ownership == null, cancellationToken)
            + await db.RegionAliases.CountAsync(x => x.Ownership == null, cancellationToken)
            + await db.GameLanguageAliases.CountAsync(x => x.Ownership == null, cancellationToken);
        if (unresolved > 0)
            logger.LogWarning("{Count} historical reference relationships have unresolved ownership and were preserved. Changed provider mappings require explicit resolution; these relationships were not claimed by the importer", unresolved);
        logger.LogInformation("Bundled reference catalog initialized");
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal static async Task InitializeAsync(RomdDbContext db, Romd.Application.Common.ReferenceCatalog.IReferenceBlobStore blobs, CancellationToken ct = default, JsonElement? document = null)
    {
        var catalog = RomdCatalogInput.Parse(document ?? BundledReferenceData.Document);
        await RomdCatalogImporter.ImportAsync(db, blobs, catalog, ct);
    }
}
