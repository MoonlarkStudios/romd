namespace Romd.Admin.Application.Catalog.Sources;

/// <summary>Database-only locks, review facts and staged subscription binding in the caller's source mutation transaction.</summary>
public interface IDatSourceLifecycleStore
{
    Task<IReadOnlyList<string>> ReadRelatedCatalogIdsAsync(int sourceId, CancellationToken ct);
    Task<bool> TryLockCatalogAsync(string catalogId, CancellationToken ct);
    Task<bool> TryLockSourceAsync(int sourceId, CancellationToken ct);
    Task LockAffectedTitlesAsync(int sourceId, CancellationToken ct);
    Task<SourceRemovalImpact> ReadImpactAsync(int datId, int sourceId, string name, string action, CancellationToken ct);
    Task<IReadOnlyList<int>> ReadVersionIdsAsync(int sourceId, CancellationToken ct);
    Task BindRelatedSubscriptionsStagedAsync(int sourceId, CancellationToken ct);
}
