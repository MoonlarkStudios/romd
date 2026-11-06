namespace Romd.Storage;

/// <summary>A read-only snapshot of an object. Dispose after all outstanding reads complete.</summary>
/// <remarks>
/// Concurrent positional reads are safe, but a reader may serialize them. Reads return up to the requested number of bytes,
/// or zero at/beyond EOF. These reads do not verify the whole-object SHA-256; use RetrieveAsync
/// and consume it fully for that guarantee. Opening never materializes or converts an object.
/// On cancellation or failure, the destination may contain bytes from earlier, successfully read frames.
/// Negative offsets throw ArgumentOutOfRangeException; reads after disposal throw ObjectDisposedException.
/// InvalidDataException indicates malformed data or a failed frame checksum; other I/O errors propagate.
/// </remarks>
public interface IRandomAccessContent : IAsyncDisposable
{
    long Length { get; }
    ContentReadIntegrity Integrity { get; }
    ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default);
}

public enum ContentReadIntegrity
{
    /// <summary>Raw bytes; no per-read verification.</summary>
    None,
    /// <summary>Each decoded frame is checked against the low 32 bits of its non-cryptographic XXH64 checksum.</summary>
    FrameChecksum
}

public enum ContentStorageFormat
{
    Unknown,
    Raw,
    SeekableZstandard
}
