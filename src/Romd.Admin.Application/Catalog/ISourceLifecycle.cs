using Romd.Domain.Catalog;

namespace Romd.Admin.Application.Catalog;

/// <summary>
///     Lifecycle operations on catalog sources. Status gates effective reads and subscription
///     polling, never admin mutations (docs/decisions/neutral-source-identity.md); the orphan
///     reads support the deleting command's transactional orphan policy.
/// </summary>
public interface ISourceLifecycle
{
    /// <summary>Retain identities with local payload before removing their source evidence. Requires refreshed payload assertions and a transaction.</summary>
    Task PreserveOwnedTitleIdentitiesAsync(int catalogSourceId, CancellationToken cancellationToken = default);

    /// <summary>Gets the source's status, or null when the source does not exist.</summary>
    Task<CatalogSourceStatus?> GetStatusAsync(int catalogSourceId, CancellationToken cancellationToken = default);

    /// <summary>Sets the status. Idempotent; participates in the caller's ambient transaction.</summary>
    Task SetStatusAsync(int catalogSourceId, CatalogSourceStatus status, CancellationToken cancellationToken = default);

    /// <summary>Distinct platforms of the source's entries, for convergence marking.</summary>
    Task<IReadOnlyList<int>> GetEntryPlatformIdsAsync(int catalogSourceId, CancellationToken cancellationToken = default);

    /// <summary>Title ids currently linked through the source's entries (truth-level).</summary>
    Task<IReadOnlyList<int>> GetLinkedTitleIdsAsync(int catalogSourceId, CancellationToken cancellationToken = default);

    /// <summary>Of the candidates, the titles that now have zero links (orphaned, truth-level).</summary>
    Task<IReadOnlyList<int>> GetOrphanedTitleIdsAsync(
        IReadOnlyList<int> candidateTitleIds,
        CancellationToken cancellationToken = default);
}
