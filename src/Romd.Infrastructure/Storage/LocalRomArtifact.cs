using Microsoft.Win32.SafeHandles;
using Romd.Storage;

namespace Romd.Infrastructure.Storage;

/// <summary>A private upload staging file, owned independently of the temporary file's cleanup handle.</summary>
internal sealed class LocalRomArtifact : IRandomAccessContent
{
    private readonly SafeFileHandle _handle;
    public long Length { get; }
    public ContentReadIntegrity Integrity => ContentReadIntegrity.None;

    public LocalRomArtifact(string path)
    {
        _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        Length = RandomAccess.GetLength(_handle);
    }

    public ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        return RandomAccess.ReadAsync(_handle, destination, offset, ct);
    }

    public ValueTask DisposeAsync()
    {
        _handle.Dispose();
        return ValueTask.CompletedTask;
    }
}
