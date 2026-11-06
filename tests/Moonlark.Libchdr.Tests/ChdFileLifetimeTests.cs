using System.Buffers;
using System.Diagnostics.Tracing;
using Xunit;

namespace Moonlark.Libchdr.Tests;

/// <summary>Qualifies that pooled decode memory never outlives, or escapes, the native reads that use it.</summary>
public sealed class ChdFileLifetimeTests
{
    /// <summary>Loads the digest-verified native test asset.</summary>
    public ChdFileLifetimeTests() => _ = NativeTestEnvironment.Root;

    /// <summary>A Dispose racing a blocked read defers release until the decode and the copy out have both finished, so no
    /// other renter receives the buffer while it is in use.</summary>
    [Fact]
    public void DisposeDuringAnInFlightReadNeverHandsItsBufferToAnotherRenter()
    {
        byte[] logical = File.ReadAllBytes(NativeTestEnvironment.Fixture("dvd-none.logical"));
        var source = new BlockingSource(File.ReadAllBytes(NativeTestEnvironment.Fixture("dvd-none.chd")));
        ChdFile file = ChdFile.Open(source, false);
        int hunkBytes = (int)file.Header.HunkBytes;
        int offset = hunkBytes * 3 + 5;
        byte[] destination = new byte[64];
        Exception? readerFault = null;
        int read = -1;
        source.Arm();
        var reader = new Thread(() =>
        {
            try
            { read = file.ReadAt(offset, destination); }
            catch (Exception error) { readerFault = error; }
        });
        using var returns = new PoolReturns();
        int returned = 0;
        bool copiedBeforeReturn = false;
        returns.Watch(reader, () =>
        {
            returned++;
            copiedBeforeReturn = logical.AsSpan(offset, destination.Length).SequenceEqual(destination);
        });
        reader.Start();
        Assert.True(source.Entered.Wait(TimeSpan.FromSeconds(10)), "the reader never entered the native read");
        file.Dispose();
        byte[] rented = ArrayPool<byte>.Shared.Rent(hunkBytes);
        Array.Fill(rented, (byte)0xEE);
        source.Release.Set();
        Assert.True(reader.Join(TimeSpan.FromSeconds(10)), "the reader did not finish");
        Assert.True(rented.AsSpan().IndexOfAnyExcept((byte)0xEE) < 0, "an in-flight native read wrote into another renter's buffer");
        ArrayPool<byte>.Shared.Return(rented);
        Assert.Null(readerFault);
        Assert.Equal(destination.Length, read);
        Assert.True(logical.AsSpan(offset, destination.Length).SequenceEqual(destination));
        Assert.Equal(1, returned);
        Assert.True(copiedBeforeReturn, "the cache went back to the pool before the copy out finished");
        Assert.Equal(1, source.Disposals);
        Assert.Throws<ObjectDisposedException>(() => file.ReadAt(0, destination));
    }

    /// <summary>The hunk cache goes back to the shared pool cleared, so later renters never see decoded bytes.</summary>
    [Fact]
    public void DisposedFilesReturnTheirHunkCacheCleared() => AssertNextRentIsClean(() =>
    {
        using ChdFile file = ChdFile.Open(new MemoryStream(CraftedChd.V4(0, CraftedChd.LegacyEntry.Stored), false), false);
        Assert.Equal(64, file.ReadAt(0, new byte[64]));
    }, Enumerable.Repeat(CraftedChd.Fill(0), CraftedChd.HunkBytes).ToArray());

    /// <summary>Integrity verification returns its decode buffer to the shared pool cleared.</summary>
    [Fact]
    public void IntegrityReturnsItsDecodeBufferCleared() => AssertNextRentIsClean(() =>
    {
        using ChdFile file = ChdFile.Open(new MemoryStream(CraftedChd.V5(CraftedChd.V5Entry.Stored, CraftedChd.V5Entry.Stored), false), false);
        _ = ChdIntegrity.Verify(file);
    }, Enumerable.Repeat(CraftedChd.Fill(1), CraftedChd.HunkBytes).ToArray());

    /// <summary>Legacy map validation returns its buffer of container map bytes to the shared pool cleared.</summary>
    [Fact]
    public void MapValidationReturnsItsBufferCleared()
    {
        const int mapOffset = 108;
        byte[] container = CraftedChd.V4(0, CraftedChd.LegacyEntry.Stored);
        AssertNextRentIsClean(() =>
        {
            using ChdFile file = ChdFile.Open(new MemoryStream(container, false), false);
        }, container[mapOffset..(mapOffset + 16)]);
    }

    /// <summary>Runs the work on a fresh thread, whose per-thread pool cache then holds only what the work returned, and checks
    /// that the next rent of the same size does not start with <paramref name="held"/>.</summary>
    private static void AssertNextRentIsClean(Action work, byte[] held)
    {
        Exception? fault = null;
        bool clean = false;
        var thread = new Thread(() =>
        {
            try
            {
                work();
                byte[] next = ArrayPool<byte>.Shared.Rent(held.Length);
                clean = !next.AsSpan(0, held.Length).SequenceEqual(held);
                ArrayPool<byte>.Shared.Return(next);
            }
            catch (Exception error) { fault = error; }
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(fault);
        Assert.True(clean, "the next renter received a buffer still holding the work's bytes");
    }

    /// <summary>Observes shared-pool returns made on one watched thread; the runtime raises them synchronously there.</summary>
    private sealed class PoolReturns : EventListener
    {
        private int _thread = -1;
        private Action? _onReturn;

        internal void Watch(Thread thread, Action onReturn)
        {
            _onReturn = onReturn;
            Volatile.Write(ref _thread, thread.ManagedThreadId);
        }

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "System.Buffers.ArrayPoolEventSource")
                EnableEvents(eventSource, EventLevel.Verbose);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventName == "BufferReturned" && Environment.CurrentManagedThreadId == Volatile.Read(ref _thread))
                _onReturn?.Invoke();
        }
    }

    private sealed class BlockingSource(byte[] bytes) : IChdDataSource
    {
        private int _armed;
        internal ManualResetEventSlim Entered { get; } = new();
        internal ManualResetEventSlim Release { get; } = new();
        internal int Disposals { get; private set; }
        public long Length => bytes.LongLength;

        internal void Arm() => Volatile.Write(ref _armed, 1);

        public int Read(long offset, Span<byte> destination)
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Entered.Set();
                Release.Wait();
            }
            int take = (int)Math.Min(destination.Length, Math.Max(0, bytes.LongLength - offset));
            bytes.AsSpan((int)offset, take).CopyTo(destination);
            return take;
        }

        public void Dispose() => Disposals++;
    }
}
