namespace Romd.Storage.FileSystem;

/// <summary>A verified, flushed representation awaiting publication under the root mutation lease.</summary>
internal sealed record StagedRepresentation(StagingFile Temp, string FinalPath, long Size, long StoredSize, bool IsCompressed)
    : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Temp.DisposeAsync();
}

/// <summary>
/// Holds either an encoded representation or, when staging found the object published, nothing but the
/// means to encode it again. The store performs publication; this type owns the staged files.
/// </summary>
internal sealed class StagedContent(
    FileSystemContentStore store,
    StorageKey key,
    long size,
    IProgress<StoreProgress>? progress,
    bool requiresRaw,
    Func<CancellationToken, Task<StagedRepresentation>> encode,
    IAsyncDisposable? spool) : IStagedContent
{
    private IAsyncDisposable? _spool = spool;
    private StoreResult? _result;
    private bool _disposed;

    public StorageKey Key { get; } = key;
    public long Size { get; } = size;
    internal IProgress<StoreProgress>? Progress { get; } = progress;
    internal bool RequiresRaw { get; } = requiresRaw;
    internal StagedRepresentation? Representation { get; private set; }

    internal async Task EncodeAsync(CancellationToken ct) => Representation = await encode(ct).ConfigureAwait(false);

    public async Task<StoreResult> PublishAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_result.HasValue)
            throw new InvalidOperationException("Staged content was already published.");
        var result = await store.PublishAsync(this, ct).ConfigureAwait(false);
        _result = result;
        // Nothing needs re-encoding now, and a deduplicated representation is garbage: release the disk
        // space before the owner finishes the work that surrounds publication.
        await ReleaseFilesAsync().ConfigureAwait(false);
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await ReleaseFilesAsync().ConfigureAwait(false);
    }

    private async ValueTask ReleaseFilesAsync()
    {
        var representation = Representation;
        var spooled = _spool;
        Representation = null;
        _spool = null;
        try
        {
            if (representation is not null)
                await representation.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (spooled is not null)
                await spooled.DisposeAsync().ConfigureAwait(false);
        }
    }
}
