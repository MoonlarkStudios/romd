using System.Buffers;
using System.Security.Cryptography;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using Microsoft.Extensions.Options;
using Romd.Domain.Hashing;
using Romd.Storage.Exceptions;

namespace Romd.Storage.FileSystem;

/// <summary>
///     File system-based content-addressable storage with deduplication, raw or seekable
///     zstd objects, positional reads, and a cross-process root mutation lease.
/// </summary>
/// <remarks>
///     <para>
///         Thread-safety: All public methods are safe to call concurrently. Hashing and compression
///         stage into exclusively locked temporary files without the root mutation lease; the
///         authoritative deduplication check, publication, deletion, and cleanup serialize across
///         processes through the lease.
///     </para>
///     <para>
///         Storage layout: Files are stored in a sharded directory structure based on the
///         first 4 hex characters of the SHA-256 hash: {root}/{xx}/{yy}/{hash}.{ext}. Writers stage
///         in {root}/.staging, so cleanup never scans the object tree.
///     </para>
/// </remarks>
internal sealed class FileSystemContentStore : IContentAddressableStore
{
    private const int DisposeStateActive = 0;
    private const int DisposeStateDisposing = 1;
    private const int DisposeStateDisposed = 2;

    /// <summary>
    /// Content of up to this many frames is decided in memory; larger content compresses this many leading frames
    /// before a full seekable candidate is written. At most 4 MiB at 1 MiB frames.
    /// </summary>
    private const int CompressionSampleFrames = 4;

    private static readonly EnumerationOptions Enumeration = new()
    {
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private readonly CancellationTokenSource _disposeCts = new();

    private readonly object _lifecycle = new();
    private readonly Dictionary<Guid, TaskCompletionSource> _mutations = new();
    private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ILogger _logger;
    private readonly ContentStoreOptions _options;
    private readonly string _rootPath;
    private readonly string _stagingPath;
    private readonly SeekIndexCache? _indexCache;

    private int _disposeState;

    public FileSystemContentStore(
        IOptions<ContentStoreOptions> options,
        ILogger<FileSystemContentStore>? logger = null)
    {
        // Capture validated settings: callers may reuse or mutate the options object later.
        var configured = options.Value;
        _options = new ContentStoreOptions
        {
            RootPath = configured.RootPath,
            WritePolicy = configured.WritePolicy,
            CompressionLevel = configured.CompressionLevel,
            SeekableFrameSize = configured.SeekableFrameSize,
            BufferSize = configured.BufferSize,
            MaxContentSize = configured.MaxContentSize,
            UncompressedExtension = configured.UncompressedExtension,
            MinCompressionRatio = configured.MinCompressionRatio,
            SeekIndexCacheBytes = configured.SeekIndexCacheBytes
        };
        Validator.ValidateObject(_options, new ValidationContext(_options), validateAllProperties: true);
        _rootPath = Path.GetFullPath(_options.RootPath);
        _stagingPath = Path.Combine(_rootPath, StagingFile.DirectoryName);
        _indexCache = _options.SeekIndexCacheBytes > 0 ? new SeekIndexCache(_options.SeekIndexCacheBytes) : null;
        _logger = logger ?? NullLogger<FileSystemContentStore>.Instance;
        if (_options.SeekableFrameSize < SeekableZstandard.MinimumFrameSize ||
            _options.SeekableFrameSize > SeekableZstandard.MaximumFrameSize)
            throw new ArgumentOutOfRangeException(nameof(options), "Seekable frame size must be 64 KiB to 1 MiB.");
        if (string.Equals(_options.UncompressedExtension, SeekableZstandard.Extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("CAS representation extensions must be distinct.", nameof(options));
        Directory.CreateDirectory(_rootPath);
    }

    #region SHA-256 Computation

    private static async ValueTask<Sha256> ComputeSha256Async(
        Stream stream,
        IProgress<long>? progress = null,
        CancellationToken ct = default)
    {
        const int bufferSize = 81920;

        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

        try
        {
            long totalBytesRead = 0;
            int read;

            while ((read = await stream.ReadAsync(buffer.AsMemory(0, bufferSize), ct).ConfigureAwait(false)) > 0)
            {
                sha256.AppendData(buffer.AsSpan(0, read));
                totalBytesRead += read;
                progress?.Report(totalBytesRead);
            }

            Span<byte> hash = stackalloc byte[Sha256.ByteLength];
            sha256.GetHashAndReset(hash);
            return Sha256.FromSpan(hash);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    #endregion

    #region IContentAddressableStore Implementation

    public async Task<StoreResult> StoreAsync(
        Stream content,
        IProgress<StoreProgress>? progress = null,
        CancellationToken ct = default)
    {
        await using var staged = await StageAsync(content, null, progress, ct).ConfigureAwait(false);
        return await staged.PublishAsync(ct).ConfigureAwait(false);
    }

    public Task<IStagedContent> StageAsync(
        Stream content,
        StorageKey? expectedKey = null,
        IProgress<StoreProgress>? progress = null,
        CancellationToken ct = default) =>
        StageCoreAsync(content, expectedKey, progress, requiresRaw: false, ct);

    public Task<IStagedContent> StageRawAsync(
        Stream content,
        StorageKey? expectedKey = null,
        IProgress<StoreProgress>? progress = null,
        CancellationToken ct = default) =>
        StageCoreAsync(content, expectedKey, progress, requiresRaw: true, ct);

    private async Task<IStagedContent> StageCoreAsync(Stream content, StorageKey? expectedKey,
        IProgress<StoreProgress>? progress, bool requiresRaw, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
        {
            throw new ArgumentException("Stream must be readable.", nameof(content));
        }

        using var operation = BeginMutation(ct);
        var linkedToken = operation.Token;

        if (content.CanSeek)
        {
            long contentLength = content.Length - content.Position;
            if (contentLength > _options.MaxContentSize)
            {
                throw new InvalidOperationException(
                    $"Content size {contentLength:N0} exceeds maximum allowed size of {_options.MaxContentSize:N0} bytes.");
            }

            return await StageSeekableAsync(content, contentLength, expectedKey, progress, requiresRaw, linkedToken)
                .ConfigureAwait(false);
        }

        return await StageNonSeekableAsync(content, expectedKey, progress, requiresRaw, linkedToken).ConfigureAwait(false);
    }

    public async Task<Stream?> RetrieveAsync(StorageKey key, CancellationToken ct = default)
    {
        ThrowIfDisposedOrDisposing();
        ct.ThrowIfCancellationRequested();
        var (handle, format) = OpenHandle(key, FileOptions.SequentialScan);
        if (handle is null)
            return null;
        try
        {
            return await CreateVerifiedStreamAsync(handle, format, key, ct).ConfigureAwait(false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public async Task<IRandomAccessContent?> OpenRandomAccessAsync(StorageKey key, CancellationToken ct = default)
    {
        long started = Stopwatch.GetTimestamp();
        ThrowIfDisposedOrDisposing();
        ct.ThrowIfCancellationRequested();
        var (handle, format) = OpenHandle(key, FileOptions.RandomAccess);
        if (handle is null)
            return null;
        try
        {
            var index = format == ContentStorageFormat.SeekableZstandard
                ? await ReadSharedIndexAsync(key, handle, ct).ConfigureAwait(false)
                : null;
            return new RandomAccessContent(handle, index);
        }
        catch (Exception ex)
        {
            if (ex is InvalidDataException)
                ContentStoreDiagnostics.CorruptionFailures.Add(1);
            handle.Dispose();
            throw;
        }
        finally { ContentStoreDiagnostics.OpenDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds); }
    }

    public ISynchronousRawContent? OpenSynchronousRaw(StorageKey key)
    {
        ThrowIfDisposedOrDisposing();
        var (handle, format) = OpenHandle(key, FileOptions.RandomAccess, asynchronous: false);
        if (handle is null)
            return null;
        try
        {
            if (format != ContentStorageFormat.Raw)
                throw new NotSupportedException("Synchronous positional reads require a raw CAS object.");
            return new SynchronousRawContent(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Positional readers open once per VFS request, so they share cached seek tables. Sequential reads
    /// amortize their table over the whole object and do not displace them.
    /// </summary>
    private async ValueTask<SeekableZstandard.Index> ReadSharedIndexAsync(StorageKey key, SafeFileHandle handle,
        CancellationToken ct)
    {
        if (_indexCache is null)
            return await SeekableZstandard.ReadIndexAsync(handle, ct).ConfigureAwait(false);
        var generation = FileGeneration.Of(handle);
        if (_indexCache.TryGet(key, generation, out var cached))
        {
            ContentStoreDiagnostics.IndexCacheHits.Add(1);
            return cached;
        }

        ContentStoreDiagnostics.IndexCacheMisses.Add(1);
        var index = await SeekableZstandard.ReadIndexAsync(handle, ct).ConfigureAwait(false);
        _indexCache.Add(key, generation, index);
        return index;
    }

    internal int CachedSeekTables => _indexCache?.Count ?? 0;

    public Task<bool> ExistsAsync(StorageKey key, CancellationToken ct = default)
    {
        ThrowIfDisposedOrDisposing();
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(ResolvePath(key).Path is not null);
    }

    // Each representation bounds itself: raw bytes end with the file, and seekable decoding stops at
    // the frames its own validated index describes, checking every frame checksum on the way.
    private async Task<Stream> CreateVerifiedStreamAsync(SafeFileHandle handle, ContentStorageFormat format,
        StorageKey key, CancellationToken ct)
    {
        Stream stream = format == ContentStorageFormat.SeekableZstandard
            ? new RandomAccessContent(handle, await SeekableZstandard.ReadIndexAsync(handle, ct).ConfigureAwait(false)).AsStream()
            : new FileStream(handle, FileAccess.Read, _options.BufferSize, isAsync: true);
        return new HashVerifyingStream(stream, key, _logger);
    }

    public async Task<bool> VerifyAsync(StorageKey key, CancellationToken ct = default)
    {
        ThrowIfDisposedOrDisposing();
        ct.ThrowIfCancellationRequested();
        var (handle, format) = OpenHandle(key, FileOptions.SequentialScan);
        if (handle is null)
            return false;
        using (handle)
        {
            await using var verified = await CreateVerifiedStreamAsync(handle, format, key, ct).ConfigureAwait(false);
            await verified.CopyToAsync(Stream.Null, _options.BufferSize, ct).ConfigureAwait(false);
        }
        return true;
    }

    public async Task<int> CleanupTemporaryFilesAsync(CancellationToken ct = default)
    {
        using var operation = BeginMutation(ct);
        ct = operation.Token;
        await using var lease = await MutationLease.AcquireAsync(_rootPath, ct).ConfigureAwait(false);
        int deleted = 0;
        if (!Directory.Exists(_stagingPath))
            return deleted;
        foreach (string path in Directory.EnumerateFiles(_stagingPath, StagingFile.Prefix + "*", Enumeration))
        {
            ct.ThrowIfCancellationRequested();
            // Staging runs outside the lease: skip files a live writer in any process still holds open.
            if (!StagingFile.IsStagingName(Path.GetFileName(path)) || !StagingFile.IsAbandoned(path))
                continue;
            File.Delete(path);
            deleted++;
        }
        return deleted;
    }

    public async IAsyncEnumerable<StorageKey> EnumerateAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        ThrowIfDisposedOrDisposing();
        var keys = new HashSet<StorageKey>();
        foreach (string first in Directory.EnumerateDirectories(_rootPath, "??", Enumeration))
            foreach (string second in Directory.EnumerateDirectories(first, "??", Enumeration))
            {
                ct.ThrowIfCancellationRequested();
                // Leaf directories hold a handful of objects; yielding between them keeps the walk cooperative.
                await Task.Yield();
                keys.Clear();
                foreach (string path in Directory.EnumerateFiles(second, "*", Enumeration))
                {
                    if (TryParseObjectPath(path, out var key) && keys.Add(key))
                        yield return key;
                }
            }
    }

    public async Task<bool> DeleteAsync(StorageKey key, CancellationToken ct = default)
    {
        using var operation = BeginMutation(ct);
        ct = operation.Token;
        await using var lease = await MutationLease.AcquireAsync(_rootPath, ct).ConfigureAwait(false);
        _indexCache?.Remove(key);
        return TryDeleteFile(GetSeekablePath(key)) | TryDeleteFile(GetRawPath(key));
    }

    public Task<ContentInfo?> GetInfoAsync(StorageKey key, CancellationToken ct = default)
    {
        ThrowIfDisposedOrDisposing();
        ct.ThrowIfCancellationRequested();

        var (handle, format) = OpenHandle(key, FileOptions.RandomAccess);
        using (handle)
        {
            return Task.FromResult(handle is null ? (ContentInfo?)null : new ContentInfo(
                key, RandomAccess.GetLength(handle),
                new DateTimeOffset(File.GetCreationTimeUtc(handle), TimeSpan.Zero),
                format != ContentStorageFormat.Raw, format));
        }
    }

    private MutationOperation BeginMutation(CancellationToken ct)
    {
        lock (_lifecycle)
        {
            ThrowIfDisposedOrDisposing();
            ct.ThrowIfCancellationRequested();
            var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
            var id = Guid.NewGuid();
            _mutations.Add(id, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            return new MutationOperation(this, id, linked);
        }
    }

    private sealed class MutationOperation(FileSystemContentStore store, Guid id, CancellationTokenSource linked)
        : IDisposable
    {
        public CancellationToken Token => linked.Token;
        public void Dispose()
        {
            linked.Dispose();
            lock (store._lifecycle)
            {
                if (store._mutations.Remove(id, out var completion))
                    completion.TrySetResult();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[]? pending = null;
        lock (_lifecycle)
        {
            if (_disposeState == DisposeStateActive)
            {
                Volatile.Write(ref _disposeState, DisposeStateDisposing);
                pending = _mutations.Values.Select(completion => completion.Task).ToArray();
            }
        }
        if (pending is null)
        {
            await _disposed.Task.ConfigureAwait(false);
            return;
        }
        try
        {
            try
            { await _disposeCts.CancelAsync().ConfigureAwait(false); }
            finally { await Task.WhenAll(pending).ConfigureAwait(false); }
        }
        finally
        {
            Volatile.Write(ref _disposeState, DisposeStateDisposed);
            _disposeCts.Dispose();
            _disposed.TrySetResult();
        }
    }

    private void ThrowIfDisposedOrDisposing()
    {
        if (Volatile.Read(ref _disposeState) != DisposeStateActive)
        {
            throw new ObjectDisposedException(nameof(FileSystemContentStore));
        }
    }

    #endregion

    #region Seekable Store

    private async Task<StagedContent> StageSeekableAsync(
        Stream content,
        long contentLength,
        StorageKey? expectedKey,
        IProgress<StoreProgress>? progress,
        bool requiresRaw,
        CancellationToken ct)
    {
        long startPosition = content.Position;
        // A trusted key skips the hashing pass; the representation writers still verify it.
        var key = expectedKey
            ?? StorageKey.FromHash(await HashAsync(content, contentLength, progress, ct).ConfigureAwait(false));
        long uncompressedSize = expectedKey.HasValue ? contentLength : content.Position - startPosition;

        var staged = new StagedContent(this, key, uncompressedSize, progress, requiresRaw, async innerCt =>
        {
            content.Position = startPosition;
            return await StageRepresentationAsync(content, key.Hash, contentLength, progress, requiresRaw, innerCt)
                .ConfigureAwait(false);
        }, spool: null);
        return await EncodeUnlessPublishedAsync(staged, ct).ConfigureAwait(false);
    }

    private async Task<Sha256> HashAsync(Stream content, long contentLength, IProgress<StoreProgress>? progress,
        CancellationToken ct)
    {
        var hashProgress = progress != null
            ? new Progress<long>(bytes => progress.Report(new StoreProgress
            {
                Phase = StorePhase.Hashing,
                BytesProcessed = bytes,
                TotalBytes = contentLength
            }))
            : null;

        using var bounded = new BoundedReadStream(content, _options.MaxContentSize, leaveOpen: true);
        return await ComputeSha256Async(bounded, hashProgress, ct).ConfigureAwait(false);
    }

    private async Task<StagedRepresentation> StageRepresentationAsync(
        Stream content,
        Sha256 sha256,
        long? totalSize,
        IProgress<StoreProgress>? progress,
        bool requiresRaw,
        CancellationToken ct)
    {
        var key = StorageKey.FromHash(sha256);
        if (requiresRaw || _options.WritePolicy == ContentWritePolicy.Raw)
            return await StageRawAsync(content, key, totalSize, progress, ct).ConfigureAwait(false);

        long startPosition = content.Position;
        long remaining = content.Length - startPosition;
        if (remaining <= SampleBytes &&
            await TryStageInMemoryAsync(content, key, (int)remaining, totalSize, progress, ct).ConfigureAwait(false)
                is { } decided)
            return decided;
        if (await PrefixMissesCompressionRatioAsync(content, startPosition, ct).ConfigureAwait(false))
            return await StageRawAsync(content, key, totalSize, progress, ct).ConfigureAwait(false);
        string compressedPath = GetSeekablePath(key);
        var staged = StagingFile.Create(_stagingPath, _options.BufferSize);
        try
        {
            long uncompressedSize = await SeekableZstandard.WriteAsync(content, staged.Stream,
                _options.SeekableFrameSize, _options.CompressionLevel, _options.MaxContentSize,
                key, progress, totalSize, ct).ConfigureAwait(false);
            long compressedSize = staged.Stream.Length;
            double ratio = uncompressedSize > 0 ? compressedSize / (double)uncompressedSize : 1.0;
            if (ratio <= _options.MinCompressionRatio)
            {
                // Only the kept candidate is synced; a discarded one never forces its bytes to disk.
                staged.Sync();
                return new StagedRepresentation(staged, compressedPath, uncompressedSize, compressedSize, true);
            }
        }
        catch
        {
            await staged.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        // Compression saved too little: discard the seekable candidate and stage raw bytes instead.
        await staged.DisposeAsync().ConfigureAwait(false);
        content.Position = startPosition;
        return await StageRawAsync(content, key, totalSize, progress, ct).ConfigureAwait(false);
    }

    private int SampleBytes => CompressionSampleFrames * _options.SeekableFrameSize;

    /// <summary>
    /// Decides the representation of content no larger than the compression sample in memory, so only the kept
    /// representation is written and synced. Content that outgrows its reported length is rewound and left to the
    /// streaming writers (null).
    /// </summary>
    private async Task<StagedRepresentation?> TryStageInMemoryAsync(Stream content, StorageKey key, int length,
        long? totalSize, IProgress<StoreProgress>? progress, CancellationToken ct)
    {
        long startPosition = content.Position;
        byte[] input = ArrayPool<byte>.Shared.Rent(length);
        byte[] encoded = ArrayPool<byte>.Shared.Rent(SeekableZstandard.EncodedBound(length, _options.SeekableFrameSize));
        try
        {
            int size = await content.ReadAtLeastAsync(input.AsMemory(0, length), length, throwOnEndOfStream: false, ct)
                .ConfigureAwait(false);
            // The encoding buffer is still scratch: a one-byte probe observes the end of the content, as the
            // streaming writers do.
            if (size == length && await content.ReadAsync(encoded.AsMemory(0, 1), ct).ConfigureAwait(false) > 0)
            {
                content.Position = startPosition;
                return null;
            }

            int encodedSize = SeekableZstandard.Encode(input.AsSpan(0, size), encoded, _options.SeekableFrameSize,
                _options.CompressionLevel, key);
            double ratio = size > 0 ? encodedSize / (double)size : 1.0;
            bool compressed = ratio <= _options.MinCompressionRatio;
            var kept = compressed ? encoded.AsMemory(0, encodedSize) : input.AsMemory(0, size);
            var staged = StagingFile.Create(_stagingPath, _options.BufferSize);
            try
            {
                await staged.Stream.WriteAsync(kept, ct).ConfigureAwait(false);
                staged.Sync();
            }
            catch
            {
                await staged.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            progress?.Report(new StoreProgress
            {
                Phase = compressed ? StorePhase.Compressing : StorePhase.Writing,
                BytesProcessed = size,
                TotalBytes = totalSize
            });
            return new StagedRepresentation(staged, compressed ? GetSeekablePath(key) : GetRawPath(key), size,
                kept.Length, compressed);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(input);
            ArrayPool<byte>.Shared.Return(encoded);
        }
    }

    /// <summary>
    /// Compresses the leading frames of content larger than the sample with the writer's framing and level, so
    /// incompressible content stages raw without writing a full seekable candidate. Rewinds after sampling. A
    /// passing sample is no verdict: the whole-content ratio still decides.
    /// </summary>
    private async Task<bool> PrefixMissesCompressionRatioAsync(Stream content, long startPosition, CancellationToken ct)
    {
        double ratio = await SeekableZstandard.SampleRatioAsync(content, _options.SeekableFrameSize,
            _options.CompressionLevel, CompressionSampleFrames, ct).ConfigureAwait(false);
        content.Position = startPosition;
        return ratio > _options.MinCompressionRatio;
    }

    private async Task<StagedRepresentation> StageRawAsync(Stream source, StorageKey key, long? totalSize,
        IProgress<StoreProgress>? progress, CancellationToken ct)
    {
        string path = GetRawPath(key);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(_options.BufferSize);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var bounded = new BoundedReadStream(source, _options.MaxContentSize, leaveOpen: true);
        long size = 0;
        var staged = StagingFile.Create(_stagingPath, _options.BufferSize);
        try
        {
            int read;
            while ((read = await bounded.ReadAsync(buffer.AsMemory(0, _options.BufferSize), ct).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer.AsSpan(0, read));
                await staged.Stream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                size += read;
                progress?.Report(new StoreProgress
                {
                    Phase = StorePhase.Writing,
                    BytesProcessed = size,
                    TotalBytes = totalSize
                });
            }
            var actual = Sha256.FromSpan(hash.GetHashAndReset());
            if (actual != key.Hash)
                throw new ContentCorruptedException(key, key.Hash, actual);
            staged.Sync();
            return new StagedRepresentation(staged, path, size, size, false);
        }
        catch
        {
            await staged.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ReportComplete(IProgress<StoreProgress>? progress, long size, long? totalSize)
        => progress?.Report(new StoreProgress
        {
            Phase = StorePhase.Complete,
            BytesProcessed = size,
            TotalBytes = totalSize
        });

    #endregion

    #region Non-Seekable Store

    private async Task<StagedContent> StageNonSeekableAsync(
        Stream content,
        StorageKey? expectedKey,
        IProgress<StoreProgress>? progress,
        bool requiresRaw,
        CancellationToken ct)
    {
        var spool = StagingFile.Create(_stagingPath, _options.BufferSize);
        Sha256 sha256;
        long uncompressedSize;
        try
        {
            (sha256, uncompressedSize) = await SpoolAsync(content, spool, progress, ct).ConfigureAwait(false);
            if (expectedKey is { } expected && expected.Hash != sha256)
                throw new ContentCorruptedException(expected, expected.Hash, sha256);
        }
        catch
        {
            await spool.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        // The handle owns the spool; the representation rereads it through its locked handle.
        var staged = new StagedContent(this, StorageKey.FromHash(sha256), uncompressedSize, progress, requiresRaw, innerCt =>
        {
            spool.Stream.Position = 0;
            return StageRepresentationAsync(spool.Stream, sha256, uncompressedSize, progress, requiresRaw, innerCt);
        }, spool);
        return await EncodeUnlessPublishedAsync(staged, ct).ConfigureAwait(false);
    }

    /// <summary>Spools to a staging file while computing the hash: one pass over input of unknown length.</summary>
    private async Task<(Sha256 Hash, long Size)> SpoolAsync(Stream content, StagingFile spool,
        IProgress<StoreProgress>? progress, CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(_options.BufferSize);
        try
        {
            using var boundedContent = new BoundedReadStream(content, _options.MaxContentSize, true);
            await using var hashingStream = new Sha256WriteStream(spool.Stream, true);
            int read;
            while ((read = await boundedContent.ReadAsync(buffer.AsMemory(0, _options.BufferSize), ct)
                       .ConfigureAwait(false)) > 0)
            {
                await hashingStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);

                progress?.Report(new StoreProgress
                {
                    Phase = StorePhase.Hashing,
                    BytesProcessed = hashingStream.BytesWritten,
                    TotalBytes = null
                });
            }

            return (hashingStream.GetSha256Hash(), hashingStream.BytesWritten);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    #endregion

    #region Store Coordination

    /// <summary>Encodes unless a lock-free probe, as readers resolve objects, finds the object published.</summary>
    private async Task<StagedContent> EncodeUnlessPublishedAsync(StagedContent staged, CancellationToken ct)
    {
        try
        {
            // Compression and the raw fallback run without the lease, so they never block other mutations.
            if (await GetInfoAsync(staged.Key, ct).ConfigureAwait(false) is { } existing)
                RequireCompatibleRepresentation(staged, existing);
            else
                await staged.EncodeAsync(ct).ConfigureAwait(false);
            return staged;
        }
        catch
        {
            await staged.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal async Task<StoreResult> PublishAsync(StagedContent staged, CancellationToken ct)
    {
        using var operation = BeginMutation(ct);
        ct = operation.Token;
        var key = staged.Key;
        if (staged.Representation is null)
        {
            // Staging found the object published. Encode again only if it has been deleted since.
            if (staged.RequiresRaw)
            {
                // Raw deduplication must observe the authoritative representation under the lease,
                // including an object deleted and recreated since staging. Encoding stays outside it.
                await using var deduplicationLease = await MutationLease.AcquireAsync(_rootPath, ct).ConfigureAwait(false);
                if (await GetInfoAsync(key, ct).ConfigureAwait(false) is { } rawCandidate)
                {
                    RequireCompatibleRepresentation(staged, rawCandidate);
                    return Deduplicated(key, staged.Size, rawCandidate);
                }
            }
            else if (await GetInfoAsync(key, ct).ConfigureAwait(false) is { } present)
                return Deduplicated(key, staged.Size, present);
            await staged.EncodeAsync(ct).ConfigureAwait(false);
        }

        var representation = staged.Representation!;
        // The lease covers only the authoritative deduplication check and the atomic publication.
        await using var lease = await MutationLease.AcquireAsync(_rootPath, ct).ConfigureAwait(false);
        // Another writer published first: the owner discards the staged copy after the lease is released.
        if (await GetInfoAsync(key, ct).ConfigureAwait(false) is { } existing)
        {
            RequireCompatibleRepresentation(staged, existing);
            return Deduplicated(key, staged.Size, existing);
        }
        ct.ThrowIfCancellationRequested();
        representation.Temp.Publish(representation.FinalPath);
        ReportComplete(staged.Progress, representation.Size, representation.Size);
        return new StoreResult(key, representation.Size, representation.StoredSize, false, representation.IsCompressed);
    }

    private static StoreResult Deduplicated(StorageKey key, long uncompressedSize, ContentInfo existing) =>
        new(key, uncompressedSize, existing.CompressedSize, true, existing.IsCompressed);

    private static void RequireCompatibleRepresentation(StagedContent staged, ContentInfo existing)
    {
        if (staged.RequiresRaw && existing.Format != ContentStorageFormat.Raw)
            throw new NotSupportedException($"Explicit raw staging cannot reuse the compressed object {staged.Key}.");
        if (staged.RequiresRaw && existing.StoredSize != staged.Size)
            throw new InvalidDataException(
                $"Explicit raw staging cannot reuse the object {staged.Key}: stored size {existing.StoredSize} differs from staged size {staged.Size}.");
    }

    #endregion

    #region Path Handling

    private string GetRawPath(StorageKey key)
    {
        string relativePath = ShardedPathLayout.GetRelativePath(key) + _options.UncompressedExtension;
        string fullPath = Path.GetFullPath(Path.Combine(_rootPath, relativePath));

        ValidatePathUnderRoot(fullPath);

        return fullPath;
    }

    private (SafeFileHandle? Handle, ContentStorageFormat Format) OpenHandle(StorageKey key, FileOptions options,
        bool asynchronous = true)
    {
        // Readers never wait for the mutation lease. Retry a resolve/open race with a concurrent
        // delete; once opened, FileShare.Delete preserves the reader snapshot.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var (path, format) = ResolvePath(key);
            if (path is null)
                return (null, format);
            try
            {
                return (File.OpenHandle(path, FileMode.Open, FileAccess.Read,
                    FileShare.Read | FileShare.Delete, asynchronous ? options | FileOptions.Asynchronous : options), format);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        throw new IOException("CAS object changed repeatedly while opening a reader.");
    }

    private string GetSeekablePath(StorageKey key)
    {
        string path = Path.GetFullPath(Path.Combine(_rootPath,
            ShardedPathLayout.GetRelativePath(key) + SeekableZstandard.Extension));
        ValidatePathUnderRoot(path);
        return path;
    }

    /// <summary>Accepts only the exact path this store would write for the parsed key.</summary>
    private bool TryParseObjectPath(string path, out StorageKey key)
    {
        key = default;
        string name = Path.GetFileName(path);
        int dot = name.IndexOf('.');
        if (dot != Sha256.ByteLength * 2 || !HexConverter.TryParse<Sha256>(name.AsSpan(0, dot), out var hash) ||
            hash.IsEmpty)
            return false;
        key = StorageKey.FromHash(hash);
        return string.Equals(path, GetSeekablePath(key), PathComparison) ||
               string.Equals(path, GetRawPath(key), PathComparison);
    }

    private (string? Path, ContentStorageFormat Format) ResolvePath(StorageKey key)
    {
        string seekable = GetSeekablePath(key);
        if (File.Exists(seekable))
            return (seekable, ContentStorageFormat.SeekableZstandard);
        string raw = GetRawPath(key);
        if (File.Exists(raw))
            return (raw, ContentStorageFormat.Raw);
        return (null, ContentStorageFormat.Unknown);
    }

    private void ValidatePathUnderRoot(string fullPath)
    {
        string resolvedRoot = Path.GetFullPath(_rootPath);
        string expectedPrefix = resolvedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? resolvedRoot
            : resolvedRoot + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(expectedPrefix, PathComparison))
        {
            throw new InvalidOperationException(
                $"Storage key resolves outside root directory. Path: {fullPath}, Root: {resolvedRoot}");
        }
    }

    private static bool TryDeleteFile(string path)
    {
        if (!File.Exists(path))
            return false;
        File.Delete(path);
        return true;
    }

    #endregion
}
