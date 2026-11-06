namespace Romd.Storage;

/// <summary>
///     Content prepared for publication: encoded into a private staging file, or found already
///     published when staged. Owned by one caller and not thread-safe. Disposal discards an
///     unpublished representation and releases any spooled source.
/// </summary>
public interface IStagedContent : IAsyncDisposable
{
    StorageKey Key { get; }

    /// <summary>Uncompressed length.</summary>
    long Size { get; }

    /// <summary>
    ///     Atomically publishes the staged representation under the root mutation lease, or deduplicates
    ///     against an object another writer published. If staging found the object already published and
    ///     it has since been deleted, this encodes it again from the retained source first. Succeeds once;
    ///     a failed attempt may be retried.
    /// </summary>
    /// <exception cref="InvalidOperationException">The content was already published.</exception>
    /// <exception cref="ObjectDisposedException">This handle or the store has been disposed.</exception>
    Task<StoreResult> PublishAsync(CancellationToken ct = default);
}
