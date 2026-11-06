using Microsoft.Win32.SafeHandles;

namespace Romd.Storage.FileSystem;

/// <summary>Owns a raw snapshot handle with synchronous positional reads and no shared cursor.</summary>
internal sealed class SynchronousRawContent : ISynchronousRawContent
{
    private readonly SafeFileHandle _handle;
    private readonly long _length;
    private int _disposed;

    internal SynchronousRawContent(SafeFileHandle handle)
    {
        _handle = handle;
        _length = RandomAccess.GetLength(handle);
        ContentStoreDiagnostics.Readers.Add(1);
    }

    public long Length
    {
        get
        {
            ThrowIfDisposed();
            return _length;
        }
    }

    public int ReadAt(long offset, Span<byte> destination)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset >= _length || destination.IsEmpty)
            return 0;
        int count = (int)Math.Min(destination.Length, _length - offset);
        int read = 0;
        while (read < count)
        {
            int received = RandomAccess.Read(_handle, destination[read..count], checked(offset + read));
            if (received == 0)
                throw new EndOfStreamException("The raw CAS snapshot ended before its recorded length.");
            read += received;
        }
        return read;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _handle.Dispose();
        ContentStoreDiagnostics.Readers.Add(-1);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
