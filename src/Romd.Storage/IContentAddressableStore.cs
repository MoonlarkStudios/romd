using Romd.Storage.Exceptions;

namespace Romd.Storage;

/// <summary>
///     Content-addressable storage where files are identified by their cryptographic hash.
///     Provides automatic deduplication and transparent compression.
/// </summary>
/// <remarks>
///     Thread-safety: All methods are safe to call concurrently. Concurrent stores of
///     identical content are coordinated to avoid duplicate work.
/// </remarks>
public interface IContentAddressableStore : IAsyncDisposable
{
    /// <summary>
    ///     Stores content and returns computed hashes.
    ///     Automatically deduplicates if content already exists.
    /// </summary>
    /// <param name="content">The content stream. Must be readable.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result containing hashes and storage metadata.</returns>
    /// <exception cref="ArgumentNullException">Content is null.</exception>
    /// <exception cref="ArgumentException">Content stream is not readable.</exception>
    /// <exception cref="InvalidOperationException">Content exceeds maximum allowed size.</exception>
    /// <exception cref="OperationCanceledException">Operation was cancelled.</exception>
    /// <exception cref="ObjectDisposedException">Store has been disposed.</exception>
    Task<StoreResult> StoreAsync(
        Stream content,
        IProgress<StoreProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    ///     Retrieves content by storage key.
    /// </summary>
    /// <param name="key">The storage key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A decompressed, verifiable stream, or null if not found. Caller must dispose.</returns>
    /// <remarks>
    ///     <para>
    ///         The returned stream verifies content integrity (SHA-256) when fully consumed.
    ///         Callers must either read the stream to completion or call <c>ForceVerify()</c> /
    ///         <c>ForceVerifyAsync()</c> on the returned stream to trigger verification.
    ///     </para>
    ///     <para>
    ///         Early disposal (before full consumption) skips verification. This is logged
    ///         at debug level but does not throw.
    ///     </para>
    /// </remarks>
    /// <exception cref="ContentCorruptedException">
    ///     Hash mismatch detected when the stream is fully read.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    ///     Content exceeds maximum allowed size during read.
    /// </exception>
    /// <exception cref="ObjectDisposedException">Store has been disposed.</exception>
    Task<Stream?> RetrieveAsync(StorageKey key, CancellationToken ct = default);

    /// <summary>
    ///     Checks if content exists.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Store has been disposed.</exception>
    Task<bool> ExistsAsync(StorageKey key, CancellationToken ct = default);

    /// <summary>
    ///     Deletes content. Returns true if deleted, false if not found.
    /// </summary>
    /// <remarks>
    ///     This is a low-level operation. Higher layers should manage reference counting
    ///     and only delete content when RefCount reaches zero after a grace period.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">Store has been disposed.</exception>
    Task<bool> DeleteAsync(StorageKey key, CancellationToken ct = default);

    /// <summary>
    ///     Gets metadata without retrieving content.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Store has been disposed.</exception>
    Task<ContentInfo?> GetInfoAsync(StorageKey key, CancellationToken ct = default);
}
