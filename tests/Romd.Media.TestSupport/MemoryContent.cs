using Romd.Storage;

namespace Romd.Media.TestSupport;

/// <summary>Byte-array content with read counting, disposal tracking and an optional per-read gate.</summary>
public sealed class MemoryContent(byte[] bytes) : IRandomAccessContent
{
    private int _reads;
    private int _disposed;

    public Func<CancellationToken, Task>? BeforeRead { get; init; }
    public int Reads => Volatile.Read(ref _reads);
    public bool Disposed => Volatile.Read(ref _disposed) != 0;
    public long Length => bytes.Length;
    public ContentReadIntegrity Integrity => ContentReadIntegrity.None;

    public async ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ct.ThrowIfCancellationRequested();
        if (BeforeRead is not null)
            await BeforeRead(ct);
        Interlocked.Increment(ref _reads);
        if (offset >= bytes.Length)
            return 0;
        int count = (int)Math.Min(destination.Length, bytes.Length - offset);
        bytes.AsMemory((int)offset, count).CopyTo(destination);
        return count;
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }
}
