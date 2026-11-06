namespace Romd.Admin.Application.Catalog;

/// <summary>
///     Rebuilds the canonical catalog projection for a platform and exposes the projection's
///     Dirty/Clean/Failed state machine.
/// </summary>
public interface ICatalogProjectionService
{
    /// <summary>
    ///     Rebuilds the catalog projection for <paramref name="platformId"/> in its own
    ///     transaction (callers must invoke this after their source mutation has committed).
    ///     The Clean completion state commits atomically with the rebuilt rows. Returns
    ///     <c>true</c> when the rebuild committed successfully. Returns <c>false</c> when the
    ///     rebuild failed and left the platform in Failed state for recovery.
    /// </summary>
    Task<bool> RebuildPlatformAsync(int platformId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Marks the platform's catalog projection Dirty within the caller's current unit of
    ///     work and transaction. The caller owns the commit.
    /// </summary>
    /// <param name="affectedTitleIds">
    ///     Exact affected titles when the mutation has a bounded title scope. Null means the
    ///     mutation can affect the platform broadly and requires a platform refresh.
    /// </param>
    Task MarkPlatformDirtyAsync(
        int platformId,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<int>? affectedTitleIds = null);

    /// <summary>
    ///     Marks a platform dirty after a source-wide topology/status mutation and rolls up
    ///     only titles linked to that source using a relational scope.
    /// </summary>
    Task MarkCatalogSourceDirtyAsync(
        int platformId,
        int catalogSourceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Refreshes provider assertions for one source after its payload graph changes.
    ///     Requires the caller's mutation transaction.
    /// </summary>
    Task RefreshCatalogSourcePayloadAsync(
        int catalogSourceId,
        CancellationToken cancellationToken = default);

    /// <summary>Refreshes provider assertions for one platform inside the caller's transaction.</summary>
    Task RefreshPlatformPayloadAsync(
        int platformId,
        CancellationToken cancellationToken = default);

    /// <summary>Gets platform IDs whose catalog projection is Dirty or Failed.</summary>
    Task<IReadOnlyList<int>> GetPlatformIdsNeedingRebuildAsync(CancellationToken cancellationToken = default);
}
