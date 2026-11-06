using System.Buffers;
using System.Diagnostics;
using System.IO.Hashing;
using Microsoft.Win32.SafeHandles;
using ZstdSharp;

namespace Romd.Storage.FileSystem;

/// <summary>
/// Owns an immutable file handle. A single decoded-frame cache bounds memory per reader and
/// avoids repeated decoding of small neighboring reads. Calls on this reader serialize; separate
/// readers execute independently. No mutable Stream.Position is shared.
/// </summary>
internal sealed class RandomAccessContent : IRandomAccessContent
{
    private readonly SafeFileHandle _handle;
    private readonly SeekableZstandard.Index? _index;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Decompressor? _decompressor;
    private byte[]? _decoded;
    private byte[]? _compressed;
    private int _cachedFrame = -1;
    private bool _disposed;

    internal RandomAccessContent(SafeFileHandle handle, SeekableZstandard.Index? index)
    {
        _handle = handle;
        _index = index;
        Length = index?.Length ?? RandomAccess.GetLength(handle);
        if (index is not null)
            _decompressor = new Decompressor();
        ContentStoreDiagnostics.Readers.Add(1);
    }

    public long Length { get; }
    public ContentReadIntegrity Integrity => _index is null
        ? ContentReadIntegrity.None : ContentReadIntegrity.FrameChecksum;

    public async ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        long started = Stopwatch.GetTimestamp();
        ContentStoreDiagnostics.RequestedBytes.Add(destination.Length);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (offset >= Length || destination.IsEmpty)
                return 0;
            int count = (int)Math.Min(destination.Length, Length - offset);
            if (_index is null)
            {
                await SeekableZstandard.ReadExactlyAsync(_handle, destination[..count], offset, ct).ConfigureAwait(false);
                ContentStoreDiagnostics.StoredBytesRead.Add(count);
                ContentStoreDiagnostics.ReturnedBytes.Add(count);
                return count;
            }

            int written = 0;
            int frameIndex = _index.FindFrame(offset);
            while (written < count)
            {
                ct.ThrowIfCancellationRequested();
                var frame = _index.GetFrame(frameIndex);
                if (_cachedFrame != frameIndex)
                {
                    _cachedFrame = -1;
                    EnsureCapacity(ref _decoded, frame.Size);
                    EnsureCapacity(ref _compressed, frame.StoredSize);
                    await SeekableZstandard.ReadExactlyAsync(_handle, _compressed.AsMemory(0, frame.StoredSize),
                        frame.Offset, ct).ConfigureAwait(false);
                    ContentStoreDiagnostics.StoredBytesRead.Add(frame.StoredSize);
                    int decoded;
                    try
                    {
                        decoded = _decompressor!.Unwrap(_compressed.AsSpan(0, frame.StoredSize),
                            _decoded.AsSpan(0, frame.Size));
                    }
                    catch (ZstdException ex)
                    {
                        throw new InvalidDataException("Invalid compressed CAS frame.", ex);
                    }
                    if (decoded != frame.Size ||
                        (uint)XxHash64.HashToUInt64(_decoded.AsSpan(0, decoded)) != frame.Checksum)
                        throw new InvalidDataException("CAS frame checksum or length mismatch.");
                    _cachedFrame = frameIndex;
                    ContentStoreDiagnostics.DecodedBytes.Add(decoded);
                }
                else
                    ContentStoreDiagnostics.CacheHits.Add(1);
                int within = (int)(offset - frame.LogicalOffset);
                int copy = Math.Min(count - written, frame.Size - within);
                _decoded.AsMemory(within, copy).CopyTo(destination[written..]);
                offset += copy;
                written += copy;
                frameIndex++;
            }
            ContentStoreDiagnostics.ReturnedBytes.Add(written);
            return written;
        }
        catch (InvalidDataException)
        {
            ContentStoreDiagnostics.CorruptionFailures.Add(1);
            throw;
        }
        finally
        {
            _gate.Release();
            ContentStoreDiagnostics.ReadDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
    }

    private static void EnsureCapacity(ref byte[]? buffer, int size)
    {
        if (buffer is not null && buffer.Length >= size)
            return;
        var previous = buffer;
        buffer = null;
        if (previous is not null)
            ArrayPool<byte>.Shared.Return(previous);
        buffer = ArrayPool<byte>.Shared.Rent(size);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            ContentStoreDiagnostics.Readers.Add(-1);
            _handle.Dispose();
            _decompressor?.Dispose();
            if (_decoded is not null)
                ArrayPool<byte>.Shared.Return(_decoded);
            if (_compressed is not null)
                ArrayPool<byte>.Shared.Return(_compressed);
            _decoded = _compressed = null;
        }
        finally { _gate.Release(); }
    }
}
