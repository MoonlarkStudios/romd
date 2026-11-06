namespace Moonlark.Libchdr;

/// <summary>Options for opening a read-only CHD file.</summary>
public sealed record ChdOpenOptions
{
    /// <summary>A ceiling on libchdr's compressed read-ahead memory, in bytes. Zero disables read-ahead.</summary>
    /// <remarks>libchdr allocates the whole window up front, so the ceiling is clamped to the source length. A window
    /// smaller than one hunk stays disabled, and if the allocation fails read-ahead stays off and the file still opens.
    /// <see cref="ChdFile.ReadAheadBytes"/> reports the budget actually taken. Each forward miss refills the whole window
    /// with one source read, so the ceiling also bounds first-read latency. A source read is at most
    /// <see cref="int.MaxValue"/> bytes and a larger refill falls back to direct reads, so a window of 2 GiB or more
    /// refills only once less than 2 GiB of the file remains past a miss, and then reads that whole remainder in one call.
    /// A few MiB suits sequential scans.</remarks>
    public ulong ReadAheadBytes { get; init; }
}
