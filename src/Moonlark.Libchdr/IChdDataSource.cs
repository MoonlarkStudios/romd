namespace Moonlark.Libchdr;

/// <summary>A seekable source with independent positional reads.</summary>
/// <remarks>Reads must not depend on a shared cursor. The wrapper owns disposal unless opened with leaveOpen.
/// The bytes must not change while a <see cref="ChdFile"/> reads them: the hunk map is validated at open, and changed
/// bytes could reintroduce maps that crash or hang native code. One <see cref="ChdFile"/> never reads its source
/// concurrently; a source shared by several instances must tolerate concurrent <see cref="Read"/> calls.</remarks>
public interface IChdDataSource : IDisposable
{
    /// <summary>The byte length of the source, read once when the CHD opens.</summary>
    long Length { get; }

    /// <summary>Reads at an absolute offset; a short read is permitted.</summary>
    /// <param name="offset">A nonnegative absolute byte offset.</param>
    /// <param name="destination">The memory to fill during this call.</param>
    /// <returns>The number of bytes read, between zero and the destination length.</returns>
    /// <remarks>The requested range always lies within <see cref="Length"/>, wherever a malformed CHD points. The source must
    /// not retain the span. Exceptions are restored on the managed side of a native call.</remarks>
    int Read(long offset, Span<byte> destination);
}
