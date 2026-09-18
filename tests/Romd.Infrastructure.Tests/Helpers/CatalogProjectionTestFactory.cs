using Microsoft.Extensions.Logging.Abstractions;
using Romd.Persistence;
using Romd.Infrastructure.Source;

namespace Romd.Infrastructure.Tests.Helpers;

internal static class CatalogProjectionTestFactory
{
    public static CatalogProjectionService Create(
        RomdDbContext context,
        TimeProvider? timeProvider = null) =>
        new(
            context,
            new CatalogSourceSnapshotReader([new DatCatalogSourceSnapshotProvider(context)]),
            new TitlePayloadAvailabilityProjection(
                context,
                new CatalogPayloadAssertionReader(context, [new DatCatalogPayloadAssertionProvider(context)])),
            timeProvider ?? TimeProvider.System,
            NullLogger<CatalogProjectionService>.Instance);
}
