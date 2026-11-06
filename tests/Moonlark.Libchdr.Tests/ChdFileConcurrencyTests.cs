using Xunit;

namespace Moonlark.Libchdr.Tests;

/// <summary>Qualifies that overlapping reads of one ChdFile fail fast instead of entering native code twice.</summary>
/// <remarks>libchdr keeps per-file decode state, so overlapping native reads can corrupt it or crash the process.</remarks>
public sealed class ChdFileConcurrencyTests
{
    /// <summary>Loads the digest-verified native test asset.</summary>
    public ChdFileConcurrencyTests() => _ = NativeTestEnvironment.Root;

    /// <summary>While a hunk read is inside native code, every read of the file, including metadata and cache hits, throws.</summary>
    [Fact]
    public void ReadsOverlappingAHunkReadFailFast() => WhileReading((file, hunkBytes) => file.ReadHunk(3, new byte[hunkBytes]), file =>
    {
        byte[] other = new byte[file.Header.HunkBytes];
        using var stream = new ChdStream(file, leaveOpen: true);
        Assert.Throws<InvalidOperationException>(() => file.ReadHunk(0, other));
        Assert.Throws<InvalidOperationException>(() => file.ReadAt(0, other));
        Assert.Throws<InvalidOperationException>(() => file.ReadAt(1, new byte[1]));
        Assert.Throws<InvalidOperationException>(() => stream.ReadByte());
        Assert.Throws<InvalidOperationException>(() => ChdIntegrity.Verify(file));
        Assert.Throws<InvalidOperationException>(() => file.TryGetMetadata(ChdMetadataTag.Wildcard, 0, new byte[64], out _));
    });

    /// <summary>A ReadAt holds its claim across its whole decode, so other reads throw while it is inside native code.</summary>
    [Fact]
    public void ReadsOverlappingAReadAtFailFast() => WhileReading((file, hunkBytes) => file.ReadAt(3L * hunkBytes, new byte[64]), file =>
    {
        Assert.Throws<InvalidOperationException>(() => file.ReadHunk(0, new byte[file.Header.HunkBytes]));
        Assert.Throws<InvalidOperationException>(() => file.ReadAt(0, new byte[1]));
    });

    /// <summary>A metadata read holds the claim while it reads its source, so a hunk read overlapping it throws.</summary>
    [Fact]
    public void ReadsOverlappingAMetadataReadFailFast() => WhileReading(
        (file, unused) => file.TryGetMetadata(ChdMetadataTag.Wildcard, 0, new byte[64], out _),
        file => Assert.Throws<InvalidOperationException>(() => file.ReadHunk(0, new byte[file.Header.HunkBytes])));

    /// <summary>A read that re-enters the same file from inside a source callback throws, and the outer read recovers.</summary>
    [Fact]
    public void ReentrantReadsFromASourceCallbackThrow()
    {
        var source = new GateSource(File.ReadAllBytes(NativeTestEnvironment.Fixture("dvd-none.chd")));
        using ChdFile file = ChdFile.Open(source, false);
        Exception? reentrant = null;
        source.OnArmedRead = () =>
        {
            try
            { file.ReadAt(0, new byte[1]); }
            catch (Exception error) { reentrant = error; }
        };
        source.Arm(GateMode.Callback);
        file.ReadHunk(3, new byte[file.Header.HunkBytes]);
        Assert.IsType<InvalidOperationException>(reentrant);
        Assert.Equal(1, file.ReadAt(0, new byte[1]));
    }

    /// <summary>A hunk or metadata read whose source faults releases its claim, so the next read of the file succeeds.</summary>
    [Fact]
    public void FaultedReadsReleaseTheirClaim()
    {
        var source = new GateSource(File.ReadAllBytes(NativeTestEnvironment.Fixture("dvd-none.chd")));
        using ChdFile file = ChdFile.Open(source, false);
        int hunkBytes = (int)file.Header.HunkBytes;
        source.Arm(GateMode.Fault);
        Assert.Throws<IOException>(() => file.ReadHunk(2, new byte[hunkBytes]));
        Assert.Equal(1, file.ReadAt(2L * hunkBytes, new byte[1]));
        source.Arm(GateMode.Fault);
        Assert.Throws<IOException>(() => file.ReadAt(3L * hunkBytes, new byte[8]));
        Assert.Equal(1, file.ReadAt(0, new byte[1]));
        source.Arm(GateMode.Fault);
        Assert.Throws<IOException>(() => file.TryGetMetadata(ChdMetadataTag.Wildcard, 0, new byte[64], out _));
        Assert.Equal(1, file.ReadAt(hunkBytes, new byte[1]));
    }

    /// <summary>Primes the hunk cache, blocks <paramref name="read"/> inside its source read on a background thread, runs
    /// <paramref name="overlapping"/>, checks the blocked read still holds its claim after every failed claim, then
    /// checks the blocked read finished and the file still reads exactly.</summary>
    private static void WhileReading(Action<ChdFile, int> read, Action<ChdFile> overlapping)
    {
        byte[] logical = File.ReadAllBytes(NativeTestEnvironment.Fixture("dvd-none.logical"));
        var source = new GateSource(File.ReadAllBytes(NativeTestEnvironment.Fixture("dvd-none.chd")));
        using ChdFile file = ChdFile.Open(source, false);
        int hunkBytes = (int)file.Header.HunkBytes;
        Assert.Equal(1, file.ReadAt(0, new byte[1]));
        Exception? readerFault = null;
        var reader = new Thread(() =>
        {
            try
            { read(file, hunkBytes); }
            catch (Exception error) { readerFault = error; }
        })
        { IsBackground = true };
        source.Arm(GateMode.Block);
        reader.Start();
        try
        {
            if (!source.Entered.Wait(TimeSpan.FromSeconds(10)))
            {
                reader.Join(TimeSpan.FromSeconds(1));
                Assert.Fail($"the reader never entered its source read; reader fault: {readerFault?.ToString() ?? "none"}");
            }
            overlapping(file);
            Assert.Throws<InvalidOperationException>(() => file.ReadHunk(1, new byte[hunkBytes]));
        }
        finally { source.Release.Set(); }
        Assert.True(reader.Join(TimeSpan.FromSeconds(10)), "the reader did not finish");
        Assert.Null(readerFault);
        byte[] hunk = new byte[hunkBytes];
        file.ReadHunk(0, hunk);
        Assert.True(logical.AsSpan(0, hunkBytes).SequenceEqual(hunk));
    }

    private enum GateMode { None, Callback, Block, Fault }

    /// <summary>On the first read after arming, runs a callback, blocks or throws, so a test can act while that read is in progress.</summary>
    private sealed class GateSource(byte[] bytes) : IChdDataSource
    {
        private int _armed;
        internal ManualResetEventSlim Entered { get; } = new();
        internal ManualResetEventSlim Release { get; } = new();
        internal Action? OnArmedRead { get; set; }
        public long Length => bytes.LongLength;

        internal void Arm(GateMode mode) => Volatile.Write(ref _armed, (int)mode);

        public int Read(long offset, Span<byte> destination)
        {
            switch ((GateMode)Interlocked.Exchange(ref _armed, (int)GateMode.None))
            {
                case GateMode.Callback:
                    OnArmedRead?.Invoke();
                    break;
                case GateMode.Block:
                    Entered.Set();
                    Release.Wait();
                    break;
                case GateMode.Fault:
                    throw new IOException("gate source fault");
            }
            int take = (int)Math.Min(destination.Length, Math.Max(0, bytes.LongLength - offset));
            bytes.AsSpan((int)offset, take).CopyTo(destination);
            return take;
        }

        public void Dispose() { }
    }
}
