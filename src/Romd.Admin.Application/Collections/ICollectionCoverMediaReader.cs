namespace Romd.Admin.Application.Collections;

/// <summary>Validates and retains a selected collection cover through its owning mutation transaction.</summary>
public interface ICollectionCoverMediaReader
{
    /// <summary>
    ///     Returns false when the media row is absent. Otherwise holds a key-share lock until the caller's
    ///     transaction ends, preventing a concurrent deletion from invalidating the accepted foreign key.
    ///     Requires an active transaction.
    /// </summary>
    Task<bool> LockStagedAsync(int mediaId, CancellationToken ct = default);
}
