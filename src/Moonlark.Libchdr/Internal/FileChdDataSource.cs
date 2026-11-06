using Microsoft.Win32.SafeHandles;

namespace Moonlark.Libchdr.Internal;

/// <summary>A positional file source that never uses a shared stream cursor.</summary>
internal sealed class FileChdDataSource : IChdDataSource
{
    private readonly SafeFileHandle _handle;

    internal FileChdDataSource(string path) => _handle = File.OpenHandle(path, FileMode.Open,
        FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);

    public long Length
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            return RandomAccess.GetLength(_handle);
        }
    }

    public int Read(long offset, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        _ = checked(offset + destination.Length);
        return RandomAccess.Read(_handle, destination, offset);
    }

    public void Dispose() => _handle.Dispose();
}
