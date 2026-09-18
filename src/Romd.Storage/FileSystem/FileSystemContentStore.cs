using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Romd.Domain.Hashing;
using ZstdSharp;

namespace Romd.Storage.FileSystem;

/// <summary>
///     File system-based content-addressable storage with transparent compression,
///     deduplication, and concurrent store coordination.
/// </summary>
/// <remarks>
///     <para>
///         Thread-safety: All public methods are safe to call concurrently.
///         Concurrent stores of identical content are coordinated to avoid duplicate work.
///     </para>
///     <para>
///         Storage layout: Files are stored in a sharded directory structure based on the
///         first 4 hex characters of the SHA-256 hash: {root}/{xx}/{yy}/{hash}.{ext}
///     </para>
/// </remarks>
internal sealed class FileSystemContentStore : IContentAddressableStore
{
    private const int DisposeStateActive = 0;
    private const int DisposeStateDisposing = 1;
    private const int DisposeStateDisposed = 2;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private readonly CancellationTokenSource _disposeCts = new();

    private readonly ConcurrentDictionary<Sha256, StoreOperation> _inFlightStores = new();
    private readonly ILogger _logger;
    private readonly ContentStoreOptions _options;
    private readonly string _rootPath;

    private int _disposeState;

    public FileSystemContentStore(
        IOptions<ContentStoreOptions> options,
        ILogger<FileSystemContentStore>? logger = null)
    {
        _options = options.Value;
        _rootPath = Path.GetFullPath(_options.RootPath);
        _logger = logger ?? NullLogger<FileSystemContentStore>.Instance;
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
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead)
        {
            throw new ArgumentException("Stream must be readable.", nameof(content));
        }

        ThrowIfDisposedOrDisposing();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
        var linkedToken = linkedCts.Token;

        if (content.CanSeek)
        {
            long contentLength = content.Length - content.Position;
            if (contentLength > _options.MaxContentSize)
            {
                throw new InvalidOperationException(
                    $"Content size {contentLength:N0} exceeds maximum allowed size of {_options.MaxContentSize:N0} bytes.");
            }

            return await StoreSeekableAsync(content, contentLength, progress, linkedToken).ConfigureAwait(false);
        }

        return await StoreNonSeekableAsync(content, progress, linkedToken).ConfigureAwait(false);
    }

    public Task<Stream?> RetrieveAsync(StorageKey key, CancellationToken ct = default)
    {
        ThrowIfDisposedOrDisposing();
        ct.ThrowIfCancellationRequested();

        (string? path, bool isCompressed) = GetBlobPathWithFallback(key);
        if (path == null)
        {
            return Task.FromResult<Stream?>(null);
        }

        FileStream? fileStream = null;
        try
        {
            fileStream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                _options.BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            Stream stream = fileStream;

            // Decompress if needed
            if (isCompressed)
            {
                stream = new DecompressionStream(stream);
            }

            // Enforce size limit (protects against decompression bombs and tampered files)
            stream = new BoundedReadStream(stream, _options.MaxContentSize);

            // Verify integrity on full consumption
            stream = new HashVerifyingStream(stream, key, _logger);

            return Task.FromResult<Stream?>(stream);
        }
        catch
        {
            fileStream?.Dispose();
            throw;
        }
    }

    public Task<bool> ExistsAsync(StorageKey key, CancellationToken ct = default)
    {
        ThrowIfDisposedOrDisposing();
        ct.ThrowIfCancellationRequested();

        string compressedPath = GetBlobPath(key, true);
        string uncompressedPath = GetBlobPath(key, false);

        return Task.FromResult(File.Exists(compressedPath) || File.Exists(uncompressedPath));
    }

    public Task<bool> DeleteAsync(StorageKey key, CancellationToken ct = default)
    {
        ThrowIfDisposedOrDisposing();
        ct.ThrowIfCancellationRequested();

        string compressedPath = GetBlobPath(key, true);
        string uncompressedPath = GetBlobPath(key, false);

        bool deleted = TryDeleteFile(compressedPath) | TryDeleteFile(uncompressedPath);

        return Task.FromResult(deleted);
    }

    public Task<ContentInfo?> GetInfoAsync(StorageKey key, CancellationToken ct = default)
    {
        ThrowIfDisposedOrDisposing();
        ct.ThrowIfCancellationRequested();

        (string? path, bool isCompressed) = GetBlobPathWithFallback(key);
        if (path == null)
        {
            return Task.FromResult<ContentInfo?>(null);
        }

        var info = new FileInfo(path);
        return Task.FromResult<ContentInfo?>(new ContentInfo(
            key,
            info.Length,
            new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero),
            isCompressed));
    }

    public async ValueTask DisposeAsync()
    {
        // Atomically transition from Active to Disposing
        if (Interlocked.CompareExchange(ref _disposeState, DisposeStateDisposing, DisposeStateActive) !=
            DisposeStateActive)
        {
            return; // Already disposing or disposed
        }

        try
        {
            // Signal cancellation to all in-flight operations.
            // Operations will observe this via their linked CancellationToken
            // and set their own TCS state (result, exception, or cancellation).
            await _disposeCts.CancelAsync().ConfigureAwait(false);

            // Collect actual operation tasks (not the TCS tasks)
            var actualTasks = _inFlightStores.Values
                .Select(op => op.ActualOperation)
                .Where(t => t != null)
                .Cast<Task>()
                .ToList();

            // Wait for actual operations to complete (with timeout)
            if (actualTasks.Count > 0)
            {
                try
                {
                    await Task.WhenAll(actualTasks)
                        .WaitAsync(TimeSpan.FromSeconds(30))
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning(
                        "Timed out waiting for {Count} in-flight store operations during disposal",
                        actualTasks.Count);
                }
                catch (Exception ex) when (ex is not TimeoutException)
                {
                    // Operations completed with errors - this is expected during cancellation
                    _logger.LogDebug(ex, "In-flight operations completed with errors during disposal");
                }
            }

            _inFlightStores.Clear();
        }
        finally
        {
            Volatile.Write(ref _disposeState, DisposeStateDisposed);
            _disposeCts.Dispose();
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

    private async Task<StoreResult> StoreSeekableAsync(
        Stream content,
        long contentLength,
        IProgress<StoreProgress>? progress,
        CancellationToken ct)
    {
        long startPosition = content.Position;

        // Phase 1: Hash computation with progress
        var hashProgress = progress != null
            ? new Progress<long>(bytes => progress.Report(new StoreProgress
            {
                Phase = StorePhase.Hashing, BytesProcessed = bytes, TotalBytes = contentLength
            }))
            : null;

        var sha256 = await ComputeSha256Async(content, hashProgress, ct).ConfigureAwait(false);
        long uncompressedSize = content.Position - startPosition;

        // Phase 2: Coordinated store
        return await CoordinateStoreAsync(
            sha256,
            uncompressedSize,
            async innerCt =>
            {
                content.Position = startPosition;
                return await CompressAndStoreAsync(
                    content, sha256, uncompressedSize, contentLength, progress, innerCt).ConfigureAwait(false);
            },
            ct).ConfigureAwait(false);
    }

    private async Task<StoreResult> CompressAndStoreAsync(
        Stream content,
        Sha256 sha256,
        long uncompressedSize,
        long? totalSize,
        IProgress<StoreProgress>? progress,
        CancellationToken ct)
    {
        var key = StorageKey.FromHash(sha256);
        string compressedPath = GetBlobPath(key, true);
        string uncompressedPath = GetBlobPath(key, false);

        string finalDir = Path.GetDirectoryName(compressedPath)!;
        Directory.CreateDirectory(finalDir);

        string tempPath = Path.Combine(finalDir, $".tmp-{Guid.NewGuid():N}");

        byte[] buffer = ArrayPool<byte>.Shared.Rent(_options.BufferSize);
        try
        {
            // Compress to temp file with progress
            long bytesWritten = 0;
            await using (var destStream = new FileStream(
                             tempPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             _options.BufferSize,
                             FileOptions.Asynchronous))
            await using (var compressionStream = new CompressionStream(destStream, _options.CompressionLevel))
            {
                int read;
                while ((read = await content.ReadAsync(buffer.AsMemory(0, _options.BufferSize), ct)
                           .ConfigureAwait(false)) > 0)
                {
                    await compressionStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    bytesWritten += read;

                    progress?.Report(new StoreProgress
                    {
                        Phase = StorePhase.Compressing, BytesProcessed = bytesWritten, TotalBytes = totalSize
                    });
                }
            }

            long compressedSize = new FileInfo(tempPath).Length;
            double ratio = uncompressedSize > 0 ? compressedSize / (double)uncompressedSize : 1.0;

            StoreResult result;
            if (ratio <= _options.MinCompressionRatio)
            {
                result = AtomicMoveWithDedupeCheck(
                    tempPath, compressedPath, key, uncompressedSize, compressedSize, true);
            }
            else
            {
                TryDelete(tempPath);

                string uncompressedTempPath = Path.Combine(finalDir, $".tmp-{Guid.NewGuid():N}");
                try
                {
                    if (content.CanSeek)
                    {
                        content.Position -= uncompressedSize;
                    }

                    bytesWritten = 0;
                    await using (var destStream = new FileStream(
                                     uncompressedTempPath,
                                     FileMode.Create,
                                     FileAccess.Write,
                                     FileShare.None,
                                     _options.BufferSize,
                                     FileOptions.Asynchronous))
                    {
                        int read;
                        while ((read = await content.ReadAsync(buffer.AsMemory(0, _options.BufferSize), ct)
                                   .ConfigureAwait(false)) > 0)
                        {
                            await destStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                            bytesWritten += read;

                            progress?.Report(new StoreProgress
                            {
                                Phase = StorePhase.Writing, BytesProcessed = bytesWritten, TotalBytes = totalSize
                            });
                        }
                    }

                    result = AtomicMoveWithDedupeCheck(
                        uncompressedTempPath, uncompressedPath, key,
                        uncompressedSize, uncompressedSize, false);
                }
                finally
                {
                    TryDelete(uncompressedTempPath);
                }
            }

            progress?.Report(new StoreProgress
            {
                Phase = StorePhase.Complete, BytesProcessed = uncompressedSize, TotalBytes = totalSize
            });

            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            TryDelete(tempPath);
        }
    }

    #endregion

    #region Non-Seekable Store

    private async Task<StoreResult> StoreNonSeekableAsync(
        Stream content,
        IProgress<StoreProgress>? progress,
        CancellationToken ct)
    {
        string tempPath = Path.Combine(_rootPath, $".tmp-{Guid.NewGuid():N}");

        Sha256 sha256;
        long uncompressedSize;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(_options.BufferSize);
        try
        {
            // Write to temp while computing hash (single pass, unknown total size)
            using var boundedContent = new BoundedReadStream(content, _options.MaxContentSize, true);

            await using (var tempStream = new FileStream(
                             tempPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             _options.BufferSize,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var hashingStream = new Sha256WriteStream(tempStream, true))
            {
                int read;
                while ((read = await boundedContent.ReadAsync(buffer.AsMemory(0, _options.BufferSize), ct)
                           .ConfigureAwait(false)) > 0)
                {
                    await hashingStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);

                    progress?.Report(new StoreProgress
                    {
                        Phase = StorePhase.Hashing, BytesProcessed = hashingStream.BytesWritten, TotalBytes = null
                    });
                }

                sha256 = hashingStream.GetSha256Hash();
                uncompressedSize = hashingStream.BytesWritten;
            }

            // Coordinate store
            return await CoordinateStoreAsync(
                sha256,
                uncompressedSize,
                innerCt => CompressFromTempAsync(tempPath, sha256, uncompressedSize, progress, innerCt),
                ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            TryDelete(tempPath);
        }
    }

    private async Task<StoreResult> CompressFromTempAsync(
        string tempPath,
        Sha256 sha256,
        long uncompressedSize,
        IProgress<StoreProgress>? progress,
        CancellationToken ct)
    {
        var key = StorageKey.FromHash(sha256);
        string compressedPath = GetBlobPath(key, true);
        string uncompressedPath = GetBlobPath(key, false);

        string finalDir = Path.GetDirectoryName(compressedPath)!;
        Directory.CreateDirectory(finalDir);

        string compressedTempPath = tempPath + _options.CompressedExtension;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(_options.BufferSize);
        try
        {
            long bytesProcessed = 0;
            await using (var sourceStream = new FileStream(
                             tempPath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.None,
                             _options.BufferSize,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destStream = new FileStream(
                             compressedTempPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             _options.BufferSize,
                             FileOptions.Asynchronous))
            await using (var compressionStream = new CompressionStream(destStream, _options.CompressionLevel))
            {
                int read;
                while ((read = await sourceStream.ReadAsync(buffer.AsMemory(0, _options.BufferSize), ct)
                           .ConfigureAwait(false)) > 0)
                {
                    await compressionStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    bytesProcessed += read;

                    progress?.Report(new StoreProgress
                    {
                        Phase = StorePhase.Compressing,
                        BytesProcessed = bytesProcessed,
                        TotalBytes = uncompressedSize
                    });
                }
            }

            long compressedSize = new FileInfo(compressedTempPath).Length;
            double ratio = uncompressedSize > 0 ? compressedSize / (double)uncompressedSize : 1.0;

            StoreResult result;
            if (ratio <= _options.MinCompressionRatio)
            {
                result = AtomicMoveWithDedupeCheck(
                    compressedTempPath, compressedPath, key,
                    uncompressedSize, compressedSize, true);
            }
            else
            {
                TryDelete(compressedTempPath);
                result = AtomicMoveWithDedupeCheck(
                    tempPath, uncompressedPath, key,
                    uncompressedSize, uncompressedSize, false);
            }

            progress?.Report(new StoreProgress
            {
                Phase = StorePhase.Complete, BytesProcessed = uncompressedSize, TotalBytes = uncompressedSize
            });

            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            TryDelete(compressedTempPath);
        }
    }

    #endregion

    #region Store Coordination

    private async Task<StoreResult> CoordinateStoreAsync(
        Sha256 sha256,
        long uncompressedSize,
        Func<CancellationToken, Task<StoreResult>> performStore,
        CancellationToken ct)
    {
        var key = StorageKey.FromHash(sha256);

        // Fast path: already stored
        var existingInfo = await GetInfoAsync(key, ct).ConfigureAwait(false);
        if (existingInfo.HasValue)
        {
            return new StoreResult(
                key, uncompressedSize,
                existingInfo.Value.CompressedSize,
                true,
                existingInfo.Value.IsCompressed);
        }

        var operation = new StoreOperation();

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (_inFlightStores.TryAdd(sha256, operation))
            {
                try
                {
                    var actualTask = performStore(ct);
                    operation.SetActualOperation(actualTask);

                    var result = await actualTask.ConfigureAwait(false);
                    operation.TrySetResult(result);
                    return result;
                }
                catch (OperationCanceledException)
                {
                    operation.SetCanceled();
                    throw;
                }
                catch (Exception ex)
                {
                    operation.TrySetException(ex);
                    throw;
                }
                finally
                {
                    _inFlightStores.TryRemove(sha256, out _);
                }
            }

            // Another caller is storing the same content - wait for them
            if (_inFlightStores.TryGetValue(sha256, out var existing))
            {
                try
                {
                    _logger.LogDebug("Waiting for concurrent store of {Hash}", sha256.ToShortHex());
                    return await existing.Task.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _logger.LogDebug("Concurrent store cancelled, retrying for {Hash}", sha256.ToShortHex());
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Concurrent store failed, retrying for {Hash}", sha256.ToShortHex());
                }
            }

            // Brief yield to prevent tight spinning in race condition window
            await Task.Yield();
        }
    }

    private StoreResult AtomicMoveWithDedupeCheck(
        string tempPath,
        string finalPath,
        StorageKey key,
        long uncompressedSize,
        long storedSize,
        bool isCompressed)
    {
        try
        {
            File.Move(tempPath, finalPath, false);
            return new StoreResult(
                key, uncompressedSize, storedSize,
                false, isCompressed);
        }
        catch (IOException) when (File.Exists(finalPath))
        {
            _logger.LogDebug("Race detected during store, using existing file for {Hash}", key);
            return new StoreResult(
                key, uncompressedSize,
                new FileInfo(finalPath).Length,
                true, isCompressed);
        }
    }

    private sealed class StoreOperation
    {
        private readonly TaskCompletionSource<StoreResult> _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private Task<StoreResult>? _actualOperation;

        public Task<StoreResult> Task => _tcs.Task;
        public Task<StoreResult>? ActualOperation => Volatile.Read(ref _actualOperation);

        public void SetActualOperation(Task<StoreResult> task)
            => Interlocked.CompareExchange(ref _actualOperation, task, null);

        public void TrySetResult(StoreResult result) => _tcs.TrySetResult(result);
        public void TrySetException(Exception ex) => _tcs.TrySetException(ex);
        public void SetCanceled() => _tcs.TrySetCanceled();
    }

    #endregion

    #region Path Handling

    private string GetBlobPath(StorageKey key, bool compressed)
    {
        string extension = compressed ? _options.CompressedExtension : _options.UncompressedExtension;
        string relativePath = ShardedPathLayout.GetRelativePath(key) + extension;
        string fullPath = Path.GetFullPath(Path.Combine(_rootPath, relativePath));

        ValidatePathUnderRoot(fullPath);

        return fullPath;
    }

    private (string? Path, bool IsCompressed) GetBlobPathWithFallback(StorageKey key)
    {
        string compressedPath = GetBlobPath(key, true);
        if (File.Exists(compressedPath))
        {
            return (compressedPath, true);
        }

        string uncompressedPath = GetBlobPath(key, false);
        if (File.Exists(uncompressedPath))
        {
            return (uncompressedPath, false);
        }

        return (null, false);
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

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch
        {
            // File may have been deleted by another process
        }

        return false;
    }

    #endregion
}
