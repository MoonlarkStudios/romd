namespace Romd.Application.Common.ReferenceCatalog;

/// <summary>
///     Reference asset bytes. Publication goes through the file ledger inside the caller's transaction,
///     so each asset's file row and its reference commit together under the file's lock.
/// </summary>
public interface IReferenceBlobStore
{
    /// <summary>
    ///     Publishes every content in one batch and stages their ledger rows. Requires the caller's
    ///     transaction, which holds the files' locks until it ends. Publish all of a transaction's assets in
    ///     one call, so their locks are taken in one global order.
    ///     The bootstrap importer owns the required identity flush, reads the receipt, and disposes
    ///     local staging before recording references. This adapter never flushes or commits.
    /// </summary>
    Task<IStagedReferencePublication> PublishStagedAsync(IReadOnlyList<byte[]> contents, CancellationToken ct);

    Task<byte[]?> ReadAsync(string hash, CancellationToken ct);
}

/// <summary>Ordered file identities in the original transaction, owning all local publication staging.</summary>
public interface IStagedReferencePublication : IAsyncDisposable
{
    bool RequiresFlush { get; }
    IReadOnlyList<int> ReadAfterFlush();
}
