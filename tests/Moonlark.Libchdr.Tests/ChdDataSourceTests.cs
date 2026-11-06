using Moonlark.Libchdr.Internal;
using Xunit;

namespace Moonlark.Libchdr.Tests;

/// <summary>Checks positional sources with disposable synthetic files and streams.</summary>
public sealed class ChdDataSourceTests
{
    /// <summary>File reads have independent absolute offsets and normal EOF behavior.</summary>
    [Fact]
    public void FileReadsArePositionalAndBounded()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, [0, 1, 2, 3, 4, 5]);
            using var source = new FileChdDataSource(path);
            Span<byte> buffer = stackalloc byte[4];
            Assert.Equal(6, source.Length);
            Assert.Equal(2, source.Read(4, buffer));
            Assert.Equal((byte)4, buffer[0]);
            Assert.Equal((byte)5, buffer[1]);
            Assert.Equal(4, source.Read(0, buffer));
            Assert.Equal((byte)0, buffer[0]);
            Assert.Equal(0, source.Read(6, buffer));
            Assert.Equal(0, source.Read(100, buffer));
            Assert.Equal(0, source.Read(2, Span<byte>.Empty));
        }
        finally { File.Delete(path); }
    }

    /// <summary>File source arithmetic is checked and disposed sources reject all reads.</summary>
    [Fact]
    public void FileSourceRejectsInvalidOffsetsAndUseAfterDisposal()
    {
        string path = Path.GetTempFileName();
        try
        {
            using var source = new FileChdDataSource(path);
            Assert.Throws<ArgumentOutOfRangeException>(() => source.Read(-1, new byte[1]));
            Assert.Throws<OverflowException>(() => source.Read(long.MaxValue, new byte[1]));
            source.Dispose();
            source.Dispose();
            Assert.Throws<ObjectDisposedException>(() => source.Length);
            Assert.Throws<ObjectDisposedException>(() => source.Read(0, Array.Empty<byte>()));
        }
        finally { File.Delete(path); }
    }

    /// <summary>Steady positional file reads allocate no managed bytes.</summary>
    [Fact]
    public void FileReadsAllocateNothingAfterWarmup()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, new byte[32]);
            using var source = new FileChdDataSource(path);
            Span<byte> buffer = stackalloc byte[32];
            for (int index = 0; index < 100; index++)
                source.Read(0, buffer);
            // One-time runtime transitions (such as tiering under load) may allocate once; a steady-state
            // allocation recurs in every window, so at least one window must allocate nothing.
            bool steady = false;
            for (int window = 0; window < 5 && !steady; window++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int index = 0; index < 1_000; index++)
                    source.Read(0, buffer);
                steady = GC.GetAllocatedBytesForCurrentThread() == before;
            }
            Assert.True(steady, "File reads allocated in every steady-state window.");
        }
        finally { File.Delete(path); }
    }

    /// <summary>Stream reads preserve the caller cursor and may return a short result.</summary>
    [Fact]
    public void StreamReadsPreserveCursorAndShortReads()
    {
        using var stream = new ShortStream([0, 1, 2, 3, 4, 5]);
        stream.Position = 5;
        using var source = new StreamChdDataSource(stream, leaveOpen: true);
        Span<byte> buffer = stackalloc byte[4];
        Assert.Equal(6, source.Length);
        Assert.Equal(2, source.Read(1, buffer));
        Assert.Equal((byte)1, buffer[0]);
        Assert.Equal((byte)2, buffer[1]);
        Assert.Equal(5, stream.Position);
        Assert.Equal(0, source.Read(6, buffer));
        Assert.Equal(5, stream.Position);
    }

    /// <summary>All adapters over a stream coordinate their absolute reads.</summary>
    [Fact]
    public void ConcurrentAdaptersDoNotShareACursor()
    {
        using var stream = new YieldingStream(Enumerable.Range(0, 128).Select(index => (byte)index).ToArray());
        stream.Position = 7;
        using var first = new StreamChdDataSource(stream, leaveOpen: true);
        using var second = new StreamChdDataSource(stream, leaveOpen: true);
        Parallel.For(0, 2_000, index =>
        {
            byte[] buffer = new byte[1];
            int offset = index % 128;
            Assert.Equal(1, (index % 2 == 0 ? first : second).Read(offset, buffer));
            Assert.Equal((byte)offset, buffer[0]);
        });
        Assert.Equal(7, stream.Position);
    }

    /// <summary>Original read exceptions remain authoritative when cursor restoration also fails.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StreamReadFailurePreservesOriginalException(bool restoreFails)
    {
        using var stream = new FaultStream(restoreFails);
        using var source = new StreamChdDataSource(stream, leaveOpen: true);
        Assert.Same(stream.ReadFault, Assert.Throws<IOException>(() => source.Read(0, new byte[1])));
        if (!restoreFails)
            Assert.Equal(5, stream.Position);
    }

    /// <summary>Stream ownership is honored exactly once even when closure throws.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void StreamDisposalHonorsLeaveOpen(bool leaveOpen, bool closeThrows)
    {
        var stream = new CountingStream(closeThrows);
        var source = new StreamChdDataSource(stream, leaveOpen);
        if (closeThrows && !leaveOpen)
            Assert.Throws<IOException>(source.Dispose);
        else
            source.Dispose();
        source.Dispose();
        Assert.Equal(leaveOpen ? 0 : 1, stream.CloseCount);
        Assert.Throws<ObjectDisposedException>(() => source.Length);
        Assert.Throws<ObjectDisposedException>(() => source.Read(0, Array.Empty<byte>()));
    }

    /// <summary>Streams require readable seekable semantics and checked offsets.</summary>
    [Fact]
    public void StreamSourceRejectsInvalidOffsetsAndCapabilities()
    {
        using var stream = new MemoryStream(new byte[8]);
        using var source = new StreamChdDataSource(stream, leaveOpen: true);
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Read(-1, new byte[1]));
        Assert.Throws<OverflowException>(() => source.Read(long.MaxValue, new byte[1]));
        Assert.Throws<ArgumentNullException>(() => new StreamChdDataSource(null!, leaveOpen: true));
        using var nonseekable = new NonseekableStream();
        Assert.Throws<ArgumentException>(() => new StreamChdDataSource(nonseekable, leaveOpen: true));
    }

    /// <summary>Both public entry points consume owned streams even when adapter setup fails.</summary>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    public void PublicStreamOpenSetupFailureHonorsOwnership(bool leaveOpen, bool tryOpen, bool closeThrows)
    {
        var stream = new InvalidStream(closeThrows, capabilityThrows: false);
        if (tryOpen)
            Assert.Throws<ArgumentException>(() => ChdFile.TryOpen(stream, leaveOpen, out _, out _));
        else
            Assert.Throws<ArgumentException>(() => ChdFile.Open(stream, leaveOpen));
        Assert.Equal(leaveOpen ? 0 : 1, stream.CloseCount);
    }

    /// <summary>Capability getter faults retain identity and stack while owned streams close once.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void StreamSetupSourceFaultRemainsAuthoritative(bool leaveOpen, bool closeThrows)
    {
        var stream = new InvalidStream(closeThrows, capabilityThrows: true);
        Assert.Same(stream.CapabilityFault, Assert.Throws<IOException>(() => new StreamChdDataSource(stream, leaveOpen)));
        Assert.Equal(leaveOpen ? 0 : 1, stream.CloseCount);
    }

    /// <summary>Warm stream adaptation adds no allocation to positional reads.</summary>
    [Fact]
    public void StreamReadsAllocateNothingAfterWarmup()
    {
        using var stream = new MemoryStream(new byte[32]);
        using var source = new StreamChdDataSource(stream, leaveOpen: true);
        Span<byte> buffer = stackalloc byte[32];
        for (int index = 0; index < 100; index++)
            source.Read(0, buffer);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1_000; index++)
            source.Read(0, buffer);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    private sealed class NonseekableStream : MemoryStream
    {
        public override bool CanSeek => false;
    }
    private sealed class InvalidStream(bool closeThrows, bool capabilityThrows) : MemoryStream
    {
        internal int CloseCount { get; private set; }
        internal IOException CapabilityFault { get; } = new("Capability check failed.");
        public override bool CanSeek => capabilityThrows ? throw CapabilityFault : false;
        protected override void Dispose(bool disposing)
        {
            CloseCount++;
            if (closeThrows)
                throw new IOException("Close failed.");
            base.Dispose(disposing);
        }
    }
    private sealed class ShortStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(2, buffer.Length)]);
    }
    private sealed class YieldingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override int Read(Span<byte> buffer)
        {
            Thread.Yield();
            return base.Read(buffer);
        }
    }
    private sealed class FaultStream(bool restoreFails) : MemoryStream(new byte[8])
    {
        private long _position = 5;
        private bool _read;
        internal IOException ReadFault { get; } = new("Source read failed.");
        public override long Position
        {
            get => _position;
            set
            {
                if (restoreFails && _read && value == 5)
                    throw new InvalidOperationException("Restoration failed.");
                _position = value;
            }
        }
        public override int Read(Span<byte> buffer)
        {
            _read = true;
            throw ReadFault;
        }
    }
    private sealed class CountingStream(bool closeThrows) : MemoryStream(new byte[8])
    {
        internal int CloseCount { get; private set; }
        protected override void Dispose(bool disposing)
        {
            CloseCount++;
            if (closeThrows)
                throw new IOException("Source close failed.");
            base.Dispose(disposing);
        }
    }
}
