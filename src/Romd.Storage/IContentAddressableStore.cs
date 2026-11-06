using Romd.Storage.Exceptions;

namespace Romd.Storage;

/// <summary>
///     Content-addressable storage where files are identified by their cryptographic hash.
///     Provides automatic deduplication and transparent compression.
/// </summary>
/// <remarks>
///     Thread-safety: All methods are safe to call concurrently. Staging hashes and compresses
///     without the root mutation lease; publication takes it only to deduplicate and rename, and
///     deletion and cleanup take it too. Duplicate publication is avoided, but input without an
///     expected key is still hashed. Mutations serialize across cooperating processes using an
///     exclusive root lease on a local filesystem; external changes and network-filesystem locking
///     are unsupported.
/// </remarks>
public interface IContentAddressableStore : IAsyncDisposable
{
    /// <summary>
    ///     Stages content for a later <see cref="IStagedContent.PublishAsync" />, so callers can encode
    ///     outside their own locks and publish inside them. Nothing becomes readable until publication.
    /// </summary>
    /// <param name="content">The content stream. Must be readable.</param>
    /// <param name="expectedKey">
    ///     The caller's SHA-256 of the content, when known. It skips the separate hashing pass, and content
    ///     already published is not read at all: the key is trusted for deduplication. Encoding still
    ///     verifies it and throws <see cref="ContentCorruptedException" /> on mismatch.
    /// </param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    ///     A seekable <paramref name="content" /> stream must stay open and unchanged until the staged
    ///     content is disposed; publication rereads it if the object staging found is deleted meanwhile.
    ///     Non-seekable input is spooled, and the handle owns the spool.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Content is null.</exception>
    /// <exception cref="ArgumentException">Content stream is not readable.</exception>
    /// <exception cref="InvalidOperationException">Content exceeds maximum allowed size.</exception>
    /// <exception cref="ContentCorruptedException">Content does not match <paramref name="expectedKey" />.</exception>
    /// <exception cref="OperationCanceledException">Operation was cancelled.</exception>
    /// <exception cref="ObjectDisposedException">Store has been disposed.</exception>
    Task<IStagedContent> StageAsync(
        Stream content,
        StorageKey? expectedKey = null,
        IProgress<StoreProgress>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    ///     Stages an explicitly raw object through the same ownership and publication lifetime as
    ///     <see cref="StageAsync" />. An existing compressed object with the same key is refused at
    ///     staging and again under the publication mutation lease; it is never overwritten or reused.
    /// </summary>
    /// <remarks>
    ///     Ordinary write policy is unchanged. The expected-key integrity and retained-source rules
    ///     of <see cref="StageAsync" /> still apply, including re-encoding after concurrent deletion.
    ///     Implementations without explicit raw support fail closed rather than use their default policy.
    /// </remarks>
    /// <exception cref="NotSupportedException">Explicit raw staging is unsupported or the existing object is compressed.</exception>
    Task<IStagedContent> StageRawAsync(
        Stream content,
        StorageKey? expectedKey = null,
        IProgress<StoreProgress>? progress = null,
        CancellationToken ct = default) =>
        throw new NotSupportedException("This content store does not support explicit raw staging.");

    /// <summary>
    ///     Stages and publishes content, returning computed hashes.
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
    ///         Consume through EOF to trigger verification. Use VerifyAsync for an independent
    ///         verification operation without depending on a concrete stream implementation.
    ///     </para>
    ///     <para>
    ///         Early disposal (before full consumption) skips verification. This is logged
    ///         at debug level but does not throw.
    ///     </para>
    /// </remarks>
    /// <exception cref="ContentCorruptedException">
    ///     Hash mismatch detected when the stream is fully read.
    /// </exception>
    /// <exception cref="InvalidDataException">
    ///     A seekable object's seek table is invalid when opened, or a frame fails its checksum while read.
    /// </exception>
    /// <exception cref="ObjectDisposedException">Store has been disposed.</exception>
    Task<Stream?> RetrieveAsync(StorageKey key, CancellationToken ct = default);

    /// <summary>
    /// Reads an object fully and verifies its SHA-256 and, for seekable objects, the index and frame checksums.
    /// Returns false only when missing.
    /// Corruption, cancellation, and I/O failures throw; partial reads never count as verified.
    /// </summary>
    Task<bool> VerifyAsync(StorageKey key, CancellationToken ct = default);

    /// <summary>
    /// Deletes abandoned CAS temporary files while holding the root mutation lease. Staging files a live
    /// writer in any process still holds open are skipped, so this is safe while other writers are active.
    /// Returns the number removed. Only run against a root exclusively managed by compatible writers.
    /// Does not delete published objects or reconcile application reference counts.
    /// </summary>
    Task<int> CleanupTemporaryFilesAsync(CancellationToken ct = default);

    /// <summary>
    /// Opens an efficient positional reader, or null for a missing object.
    /// The caller owns the reader; it remains valid independently of the store lifetime.
    /// </summary>
    Task<IRandomAccessContent?> OpenRandomAccessAsync(StorageKey key, CancellationToken ct = default);

    /// <summary>Opens an owned synchronous raw snapshot, or null when missing.</summary>
    /// <remarks>
    /// Compressed representations throw NotSupportedException; opening never converts an object.
    /// Intended for native callbacks that cannot wait on asynchronous reads.
    /// </remarks>
    ISynchronousRawContent? OpenSynchronousRaw(StorageKey key);

    /// <summary>
    ///     Checks if content exists.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Store has been disposed.</exception>
    Task<bool> ExistsAsync(StorageKey key, CancellationToken ct = default);

    /// <summary>
    ///     Lists each published object once, without the root mutation lease. The listing is a moving
    ///     snapshot: objects published or deleted meanwhile may or may not appear, so callers acting on it
    ///     must re-check under their own coordination, as orphan cleanup does. Staging files never appear.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Store has been disposed.</exception>
    IAsyncEnumerable<StorageKey> EnumerateAsync(CancellationToken ct = default);

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
