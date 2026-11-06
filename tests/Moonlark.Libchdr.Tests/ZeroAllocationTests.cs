using Xunit;

namespace Moonlark.Libchdr.Tests;

/// <summary>Gates steady-state reads at exactly zero managed allocation over 100,000 calls per read API.</summary>
/// <remarks><see cref="GC.GetAllocatedBytesForCurrentThread"/> counts every allocation of the calling thread exactly. Each
/// measured loop runs after a warm-up that fills the decoder's lazily created buffers, and checks what each call returns so
/// the work cannot be elided. Offsets cross hunks, so both cached and decoding reads are measured.</remarks>
public sealed class ZeroAllocationTests
{
    private const int Calls = 100_000;
    private const int Warmup = 1_024;
    private const int RangeBytes = 3_000;
    private const long Stride = 6_151;

    /// <summary>Uncompressed and compressed fixtures, opened from a path or from a seekable stream. The DVD source repeats
    /// two hunk contents, so most of its hunks are self-references; every CD hunk is distinct and decodes in full.</summary>
    public static TheoryData<string, bool> Sources { get; } = new()
    {
        { "dvd-zstd.chd", false },
        { "dvd-none.chd", false },
        { "cd-cdzs.chd", false },
        { "dvd-zstd.chd", true },
    };

    /// <summary><see cref="ChdFile.ReadHunk"/> decodes every hunk in turn without allocating.</summary>
    [Theory]
    [MemberData(nameof(Sources))]
    public void ReadHunkAllocatesNothing(string fixture, bool fromStream)
    {
        using ChdFile file = Open(fixture, fromStream);
        uint hunks = file.Header.HunkCount;
        byte[] hunk = new byte[file.Header.HunkBytes];
        for (int index = 0; index < Warmup; index++)
            file.ReadHunk((uint)index % hunks, hunk);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < Calls; index++)
            file.ReadHunk((uint)index % hunks, hunk);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }

    /// <summary><see cref="ChdFile.ReadAt"/> serves cache hits, misses and hunk-crossing ranges without allocating.</summary>
    [Theory]
    [MemberData(nameof(Sources))]
    public void ReadAtAllocatesNothing(string fixture, bool fromStream)
    {
        using ChdFile file = Open(fixture, fromStream);
        long span = checked((long)file.Header.LogicalBytes) - RangeBytes;
        byte[] range = new byte[RangeBytes];
        long read = 0;
        for (int index = 0; index < Warmup; index++)
            read += file.ReadAt(index * Stride % span, range);
        Assert.Equal((long)Warmup * RangeBytes, read);
        read = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < Calls; index++)
            read += file.ReadAt(index * Stride % span, range);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal((long)Calls * RangeBytes, read);
        Assert.Equal(0, allocated);
    }

    /// <summary><see cref="ChdStream.Read(Span{byte})"/> reads sequentially through EOF and back without allocating.</summary>
    [Theory]
    [MemberData(nameof(Sources))]
    public void StreamReadAllocatesNothing(string fixture, bool fromStream)
    {
        using ChdFile file = Open(fixture, fromStream);
        using var stream = new ChdStream(file, leaveOpen: true);
        byte[] buffer = new byte[RangeBytes];
        Assert.Equal(0, ReadSequentially(stream, buffer, Warmup));
        long before = GC.GetAllocatedBytesForCurrentThread();
        int mismatches = ReadSequentially(stream, buffer, Calls);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, mismatches);
        Assert.Equal(0, allocated);
    }

    /// <summary>Reads <paramref name="calls"/> times, rewinding at EOF; returns how many reads returned an unexpected count.</summary>
    private static int ReadSequentially(ChdStream stream, byte[] buffer, int calls)
    {
        int mismatches = 0;
        long length = stream.Length;
        for (int index = 0; index < calls; index++)
        {
            if (stream.Position == length)
                stream.Seek(0, SeekOrigin.Begin);
            long expected = Math.Min(buffer.Length, length - stream.Position);
            if (stream.Read(buffer.AsSpan()) != expected)
                mismatches++;
        }
        return mismatches;
    }

    private static ChdFile Open(string fixture, bool fromStream) => fromStream
        ? ChdFile.Open(File.OpenRead(NativeTestEnvironment.Fixture(fixture)), leaveOpen: false)
        : NativeTestEnvironment.OpenFixture(fixture);
}
