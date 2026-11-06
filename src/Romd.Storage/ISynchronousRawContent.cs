namespace Romd.Storage;

/// <summary>An owned snapshot of raw CAS bytes for synchronous native callbacks.</summary>
/// <remarks>
/// Reads are positional and safe concurrently, without a shared cursor or async work.
/// They fill the destination up to EOF but do not verify the whole-object SHA-256.
/// Dispose after all reads complete. The snapshot survives store disposal and deletion;
/// external mutation of published CAS files is unsupported.
/// </remarks>
public interface ISynchronousRawContent : IDisposable
{
    /// <summary>The snapshot's byte length.</summary>
    long Length { get; }

    /// <summary>Reads raw bytes at a nonnegative offset; returns zero at or beyond EOF.</summary>
    /// <remarks>Truncation of the snapshot throws. Operations after disposal throw.</remarks>
    int ReadAt(long offset, Span<byte> destination);
}
