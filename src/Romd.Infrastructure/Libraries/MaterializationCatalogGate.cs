using Romd.Domain.Libraries;

namespace Romd.Infrastructure.Libraries;

/// <summary>
///     Gate that blocks materializing a library while any catalog projection it depends on
///     is Dirty or Failed, so a stale catalog is never materialized. A blocked library keeps
///     its durable <c>NeedsMaterialization</c> flag and recovers once the catalog projection
///     recovery dispatcher returns the platform to Clean.
/// </summary>
public static class MaterializationCatalogGate
{
    /// <summary>
    ///     Returns <c>true</c> when the library's platform scope overlaps any platform whose
    ///     catalog projection needs a rebuild. An empty platform scope spans all platforms.
    /// </summary>
    public static bool IsBlocked(
        LibraryConfiguration configuration,
        IReadOnlyCollection<int> platformIdsNeedingRebuild) =>
        platformIdsNeedingRebuild.Count > 0
        && (configuration.AllowedPlatformIds.Count == 0
            || configuration.AllowedPlatformIds.Any(platformIdsNeedingRebuild.Contains));
}
