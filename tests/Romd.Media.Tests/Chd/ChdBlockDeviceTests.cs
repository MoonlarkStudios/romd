using System.Buffers;
using System.Buffers.Binary;
using Moonlark.Libchdr;
using Romd.Media.Chd;
using Romd.Media.TestSupport;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests.Chd;

public sealed class ChdBlockDeviceTests
{
    [Theory]
    [InlineData(0, 53)]
    [InlineData(123, 8173)]
    [InlineData(4087, 53)]
    [InlineData(8190, 1100)]
    [InlineData(9189, 17)]
    [InlineData(long.MaxValue, 17)]
    public async Task ReadAtAsync_LogicalRange_MatchesIndependentBytesAndPreservesTail(long offset, int requested)
    {
        var fixture = SyntheticChdFixture.Create();
        await using var pool = new ChdReadPool(new MediaBlockCache(8192), 2, 1);
        await using var device = await Open(fixture.Source(), pool);
        byte[] expected = fixture.Expected(offset, requested);
        byte[] actual = Enumerable.Repeat((byte)0xFE, requested).ToArray();

        int read = await device.ReadAtAsync(offset, actual);

        read.ShouldBe(expected.Length);
        actual.AsSpan(0, read).ToArray().ShouldBe(expected);
        actual.AsSpan(read).IndexOfAnyExcept((byte)0xFE).ShouldBe(-1);
        device.Length.ShouldBe(fixture.LogicalBytes);
        device.Integrity.ShouldBe(ContentReadIntegrity.None);
    }

    [Fact]
    public async Task ReadAtAsync_OffsetAboveFourGiB_UsesLogicalHunkIndexWithoutTruncation()
    {
        var fixture = SyntheticChdFixture.Create(4098, 997, hunkBytes: 1 << 20, repeatFirst: true);
        await using var pool = new ChdReadPool(new MediaBlockCache(0), 1, 0);
        await using var device = await Open(fixture.Source(), pool);
        const long offset = (1L << 32) + 31;
        byte[] actual = new byte[97];

        (await device.ReadAtAsync(offset, actual)).ShouldBe(actual.Length);

        actual.ShouldBe(fixture.Expected(offset, actual.Length));
    }

    [Fact]
    public async Task ReadAtAsync_NegativeOffset_RejectsBeforeReading()
    {
        var fixture = SyntheticChdFixture.Create();
        await using var pool = new ChdReadPool(new MediaBlockCache(0), 1, 0);
        var source = fixture.Source();
        await using var device = await Open(source, pool);
        source.Arm(block: false);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => device.ReadAtAsync(-1, new byte[1]).AsTask());

        source.PayloadReads.ShouldBe(0);
    }

    [Fact]
    public async Task ReadAtAsync_PreCancelled_DoesNotDecodeOrModifyDestination()
    {
        var fixture = SyntheticChdFixture.Create();
        await using var pool = new ChdReadPool(new MediaBlockCache(0), 1, 0);
        var source = fixture.Source();
        await using var device = await Open(source, pool);
        source.Arm(block: false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        byte[] bytes = Enumerable.Repeat((byte)0xFE, 19).ToArray();
        OperationCanceledException? failure = null;

        try
        { await device.ReadAtAsync(0, bytes, cancellation.Token); }
        catch (OperationCanceledException error) { failure = error; }

        failure.ShouldNotBeNull();
        failure.CancellationToken.ShouldBe(cancellation.Token);
        bytes.AsSpan().IndexOfAnyExcept((byte)0xFE).ShouldBe(-1);
        source.PayloadReads.ShouldBe(0);
    }

    [Fact]
    public async Task OpenAsync_MalformedHeader_DisposesSourceBeforeNativeOpen()
    {
        var fixture = SyntheticChdFixture.Create();
        fixture.Bytes[0] ^= 0xFF;
        var source = fixture.Source();
        await using var pool = new ChdReadPool(new MediaBlockCache(0), 1, 0);

        var error = await Should.ThrowAsync<InvalidDataException>(() => Open(source, pool).AsTask());

        error.Message.ShouldContain("InvalidSignature");
        source.DisposeCalls.ShouldBe(1);
        pool.Snapshot.OpenedHandles.ShouldBe(0);
    }

    [Fact]
    public async Task OpenAsync_GeometryOutsideManagedRanges_DisposesSource()
    {
        var fixture = SyntheticChdFixture.Create();
        BinaryPrimitives.WriteUInt64BigEndian(fixture.Bytes.AsSpan(32), ulong.MaxValue);
        var source = fixture.Source();
        await using var pool = new ChdReadPool(new MediaBlockCache(0), 1, 0);

        await Should.ThrowAsync<InvalidDataException>(() => Open(source, pool).AsTask());

        source.DisposeCalls.ShouldBe(1);
        pool.Snapshot.LiveHandles.ShouldBe(0);
    }

    [Fact]
    public async Task ReadAtAsync_CorruptStoredHunkInCompressedMap_ThrowsNativeCrcFailureAndDoesNotCache()
    {
        var fixture = SyntheticChdFixture.Create();
        fixture.Bytes[fixture.PayloadOffset + fixture.HunkBytes + 100] ^= 0xFF;
        var source = fixture.Source();
        var cache = new MediaBlockCache(4096);
        await using var pool = new ChdReadPool(cache, 1, 1);
        await using var device = await Open(source, pool, "corrupt-artifact");
        source.Arm(block: false);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            var error = await Should.ThrowAsync<InvalidDataException>(() =>
                device.ReadAtAsync(fixture.HunkBytes, new byte[53]).AsTask());
            error.Message.ShouldContain("corrupt-artifact");
            error.Message.ShouldContain("hunk 1");
            error.InnerException.ShouldBeOfType<ChdException>().Error.ShouldBe(ChdError.DecompressionError);
            cache.UsedBytes.ShouldBe(0);
            pool.Snapshot.IdleHandles.ShouldBe(0);
            pool.Snapshot.LiveHandles.ShouldBe(0);
        }
        source.PayloadReads.ShouldBe(2);
        byte[] good = new byte[53];
        (await device.ReadAtAsync(0, good)).ShouldBe(good.Length);
        good.ShouldBe(fixture.Expected(0, good.Length));
    }

    [Fact]
    public async Task DisposeAsync_CompletedDevice_ReleasesOwnedSourceAndRejectsFurtherReads()
    {
        var fixture = SyntheticChdFixture.Create();
        var source = fixture.Source();
        var cache = new MediaBlockCache(4096);
        await using var pool = new ChdReadPool(cache, 1, 1);
        var device = await Open(source, pool);
        await device.ReadAtAsync(0, new byte[19]);

        await device.DisposeAsync();
        await device.DisposeAsync();

        source.DisposeCalls.ShouldBe(1);
        cache.UsedBytes.ShouldBe(0);
        pool.Snapshot.LiveHandles.ShouldBe(0);
        await Should.ThrowAsync<ObjectDisposedException>(() => device.ReadAtAsync(0, new byte[1]).AsTask());
    }

    [Fact]
    public async Task DisposeAsync_ActiveRead_JoinsReadBeforeClosingSource()
    {
        var fixture = SyntheticChdFixture.Create();
        var source = fixture.Source();
        await using var pool = new ChdReadPool(new MediaBlockCache(0), 1, 1);
        var device = await Open(source, pool);
        source.Arm();
        byte[] actual = new byte[53];
        Task read = device.ReadAtAsync(0, actual).AsTask();
        Task? disposal = null;
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            disposal = device.DisposeAsync().AsTask();
            disposal.IsCompleted.ShouldBeFalse();
            source.DisposeCalls.ShouldBe(0);
        }
        finally { source.Release(); }
        await read;
        await (disposal ?? device.DisposeAsync().AsTask());
        actual.ShouldBe(fixture.Expected(0, actual.Length));
        source.DisposeCalls.ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeAsync_CancelledDemand_KeepsReaderAdmissionUntilOwnedNativeWorkDrains(bool shared)
    {
        var fixture = SyntheticChdFixture.Create();
        var source = fixture.Source();
        await using var pool = new ChdReadPool(new MediaBlockCache(0), 2, 0);
        var device = await Open(source, pool, shared ? "shared" : null);
        using var cancellation = new CancellationTokenSource();
        source.Arm();
        Task<int> read = device.ReadAtAsync(0, new byte[53], cancellation.Token).AsTask();
        Task? disposal = null;
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancellation.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(() => read);
            disposal = device.DisposeAsync().AsTask();
            // Production decoder admission is released only after this content disposal completes.
            disposal.IsCompleted.ShouldBeFalse();
            pool.Snapshot.ActiveHandles.ShouldBe(1);
            source.DisposeCalls.ShouldBe(0);
        }
        finally { source.Release(); await (disposal ?? device.DisposeAsync().AsTask()); }
        source.DisposeCalls.ShouldBe(1);
        pool.Snapshot.ActiveHandles.ShouldBe(0);
    }

    [Fact]
    public async Task ReadAtAsync_AdjacentRanges_PrefetchesOneHunkAndDemandJoinsItsFlight()
    {
        var fixture = SyntheticChdFixture.Create(hunks: 5);
        var source = fixture.Source();
        var cache = new MediaBlockCache(8192);
        await using var pool = new ChdReadPool(cache, 2, 1);
        await using var device = await Open(source, pool);
        source.Arm(blockAtHunk: 1);
        Task<int>? demand = null;
        try
        {
            await Sequence(device);
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            pool.Snapshot.Speculations.ShouldBe(1);
            pool.Snapshot.SpeculativeStarted.ShouldBe(1);
            pool.Snapshot.Flights.ShouldBe(1);
            pool.Snapshot.ActiveHandles.ShouldBe(1);
            byte[] bytes = new byte[53];
            demand = device.ReadAtAsync(4096, bytes).AsTask();
            cache.Snapshot.FlightJoins.ShouldBe(1);
            cache.Snapshot.NewFactories.ShouldBe(2);
            source.PayloadReads.ShouldBe(2);
            source.Release();
            await demand;
            bytes.ShouldBe(fixture.Expected(4096, bytes.Length));
        }
        finally { source.Release(); if (demand is not null) await demand; }
    }

    [Theory]
    [InlineData(0, 500)]
    [InlineData(4096, 0)]
    [InlineData(100, 100)]
    public async Task ReadAtAsync_NonAdjacentRanges_DoNotSpeculate(long first, long second)
    {
        var fixture = SyntheticChdFixture.Create(hunks: 5);
        await using var pool = new ChdReadPool(new MediaBlockCache(16384), 2, 1);
        await using var device = await Open(fixture.Source(), pool);
        await device.ReadAtAsync(first, new byte[53]);
        await device.ReadAtAsync(second, new byte[53]);
        pool.Snapshot.SpeculativeStarted.ShouldBe(0);
    }

    [Fact]
    public async Task ReadAtAsync_AdjacentEndOfFileAndEmptyRange_DoNotSpeculatePastGeometry()
    {
        var fixture = SyntheticChdFixture.Create();
        await using var pool = new ChdReadPool(new MediaBlockCache(16384), 2, 1);
        await using var device = await Open(fixture.Source(), pool);
        await device.ReadAtAsync(fixture.LogicalBytes - 2, new byte[1]);
        await device.ReadAtAsync(fixture.LogicalBytes - 1, new byte[1]);
        await device.ReadAtAsync(fixture.LogicalBytes, new byte[1]);
        await device.ReadAtAsync(0, Memory<byte>.Empty);
        pool.Snapshot.SpeculativeStarted.ShouldBe(0);
    }

    [Fact]
    public async Task ReadAtAsync_SingleHandle_SequentialReadsNeverSpeculate()
    {
        var fixture = SyntheticChdFixture.Create();
        await using var pool = new ChdReadPool(new MediaBlockCache(8192), 1, 1);
        await using var device = await Open(fixture.Source(), pool);
        await Sequence(device);
        pool.Snapshot.SpeculativeStarted.ShouldBe(0);
        pool.Snapshot.PeakActiveHandles.ShouldBe(1);
    }

    [Fact]
    public async Task ReadAtAsync_SeekWhilePrefetchBlocked_JoinsBeforeDistinctDemandAndClose()
    {
        var fixture = SyntheticChdFixture.Create(hunks: 5);
        var source = fixture.Source();
        await using var pool = new ChdReadPool(new MediaBlockCache(0), 2, 0);
        var device = await Open(source, pool);
        source.Arm(blockAtHunk: 1);
        Task<int>? seek = null;
        Task? disposal = null;
        try
        {
            await Sequence(device);
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            seek = device.ReadAtAsync(3 * 4096, new byte[53]).AsTask();
            seek.IsCompleted.ShouldBeFalse();
            source.PayloadReads.ShouldBe(3); // Two unretained demand decodes, plus the one prefetch.
            disposal = device.DisposeAsync().AsTask();
            disposal.IsCompleted.ShouldBeFalse();
            source.DisposeCalls.ShouldBe(0);
            source.Release();
            await seek;
            await disposal;
            pool.Snapshot.SpeculativeStarted.ShouldBe(1); // Seek reset, no second speculation.
        }
        finally { source.Release(); if (seek is not null) await seek; await (disposal ?? device.DisposeAsync().AsTask()); }
        source.DisposeCalls.ShouldBe(1);
    }

    [Fact]
    public async Task ReadAtAsync_CancelledDemandThenDistinctRead_JoinsOwnedWorkWithoutSecondHandle()
    {
        var fixture = SyntheticChdFixture.Create();
        var source = fixture.Source();
        await using var pool = new ChdReadPool(new MediaBlockCache(0), 2, 0);
        await using var device = await Open(source, pool);
        using var cancellation = new CancellationTokenSource();
        source.Arm(blockAtHunk: 0);
        Task<int> cancelled = device.ReadAtAsync(0, new byte[53], cancellation.Token).AsTask();
        Task<int>? next = null;
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancellation.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(() => cancelled);
            next = device.ReadAtAsync(4096, new byte[53]).AsTask();
            next.IsCompleted.ShouldBeFalse();
            pool.Snapshot.ActiveHandles.ShouldBe(1);
            source.PayloadReads.ShouldBe(1);
        }
        finally { source.Release(); if (next is not null) await next; }
        pool.Snapshot.PeakActiveHandles.ShouldBe(1);
        pool.Snapshot.SpeculativeStarted.ShouldBe(0);
    }

    [Fact]
    public async Task ReadAtAsync_PrefetchCorruption_NeverCachesSuccessAndDemandReportsFailure()
    {
        var fixture = SyntheticChdFixture.Create();
        fixture.Bytes[fixture.PayloadOffset + fixture.HunkBytes + 100] ^= 0xFF;
        var cache = new MediaBlockCache(8192);
        await using var pool = new ChdReadPool(cache, 2, 1);
        var device = await Open(fixture.Source(), pool);
        await Sequence(device);
        // Disposal is the deterministic join, with no timing assertion about a speculative failure.
        await device.DisposeAsync();
        pool.Snapshot.SpeculativeFailed.ShouldBe(1);
        cache.UsedBytes.ShouldBe(0); // Private artifact retires, including any successful first hunk.
        await using var demand = await Open(fixture.Source(), pool);
        var failure = await Should.ThrowAsync<InvalidDataException>(() => demand.ReadAtAsync(4096, new byte[53]).AsTask());
        failure.Message.ShouldContain("hunk 1");
        pool.Snapshot.ActiveHandles.ShouldBe(0);
    }

    [Fact]
    public async Task ReadAtAsync_LargeMultiHunkRequest_RetainsOnlyOutstandingCompletionReceipt()
    {
        var fixture = SyntheticChdFixture.Create(hunks: 128);
        var cache = new MediaBlockCache(0);
        await using var pool = new ChdReadPool(cache, 1, 0);
        await using var device = await Open(fixture.Source(), pool);
        byte[] bytes = new byte[checked((int)fixture.LogicalBytes)];
        (await device.ReadAtAsync(0, bytes)).ShouldBe(bytes.Length);
        bytes.ShouldBe(fixture.Expected(0, bytes.Length));
        device.OwnedWorkCount.ShouldBe(1);
        cache.Snapshot.PeakRentedBytes.ShouldBe(4096);
        await device.DisposeAsync();
        cache.Snapshot.LiveRentedBytes.ShouldBe(0);
    }

    [Fact]
    public async Task DisposeAsync_CancelledDemandAfterNativeCompletion_JoinsUnclaimedBufferCleanup()
    {
        var fixture = SyntheticChdFixture.Create();
        var source = fixture.Source();
        var buffers = new GatedReturns();
        var cache = new MediaBlockCache(0, buffers);
        await using var pool = new ChdReadPool(cache, 2, 0);
        var device = await Open(source, pool);
        using var cancellation = new CancellationTokenSource();
        source.Arm();
        Task<int> read = device.ReadAtAsync(0, new byte[53], cancellation.Token).AsTask();
        Task? disposal = null, poolDisposal = null;
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancellation.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(() => read);
            source.Release();
            await buffers.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            pool.Snapshot.ActiveHandles.ShouldBe(0); // Native work has completed; cache cleanup has not.
            disposal = device.DisposeAsync().AsTask();
            disposal.IsCompleted.ShouldBeFalse();
            poolDisposal = pool.DisposeAsync().AsTask();
            poolDisposal.IsCompleted.ShouldBeFalse();
            source.DisposeCalls.ShouldBe(0);
        }
        finally
        {
            source.Release();
            buffers.Release();
            await (disposal ?? device.DisposeAsync().AsTask());
            if (poolDisposal is not null)
                await poolDisposal;
        }
        cache.Snapshot.LiveRentedBytes.ShouldBe(0);
        source.DisposeCalls.ShouldBe(1);
    }

    [Fact]
    public async Task ReadAtAsync_SameHunkAfterCancelledFlightRemoved_JoinsCleanupBeforeNewNativeFactory()
    {
        var fixture = SyntheticChdFixture.Create();
        var source = fixture.Source();
        var buffers = new GatedReturns();
        var cache = new MediaBlockCache(0, buffers);
        await using var pool = new ChdReadPool(cache, 2, 0);
        var device = await Open(source, pool);
        using var cancellation = new CancellationTokenSource();
        source.Arm();
        Task<int> cancelled = device.ReadAtAsync(0, new byte[53], cancellation.Token).AsTask();
        Task<int>? next = null;
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancellation.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(() => cancelled);
            source.Release();
            await buffers.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            pool.Snapshot.Flights.ShouldBe(0);
            // The old flight has left the cache map but its unclaimed buffer return remains gated.
            // Factory invocation is synchronous until first incomplete await, so this observes whether
            // the new same-key factory starts native work, without a sleep or scheduler guess.
            next = device.ReadAtAsync(0, new byte[53]).AsTask();
            pool.Snapshot.Flights.ShouldBe(0);
            pool.Snapshot.ActiveHandles.ShouldBe(0);
            source.PayloadReads.ShouldBe(1);
            next.IsCompleted.ShouldBeFalse();
        }
        finally
        {
            source.Release();
            buffers.Release();
            if (next is not null)
                await next;
            await device.DisposeAsync();
        }
        cache.Snapshot.LiveRentedBytes.ShouldBe(0);
    }

    [Fact]
    public async Task ReadAtAsync_SameHunkAfterPrefetchFlightRemoved_JoinsClaimedSpeculativeBufferCleanup()
    {
        var fixture = SyntheticChdFixture.Create();
        var source = fixture.Source();
        // Cache0 returns each of the two initial demand buffers, then the speculative buffer.
        var buffers = new GatedReturns(blockReturn: 3);
        var cache = new MediaBlockCache(0, buffers);
        await using var pool = new ChdReadPool(cache, 2, 0);
        var device = await Open(source, pool);
        source.Arm(blockAtHunk: 1);
        Task<int>? next = null;
        try
        {
            await Sequence(device);
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            source.Release();
            await buffers.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            pool.Snapshot.Flights.ShouldBe(0);
            pool.Snapshot.Speculations.ShouldBe(1); // Its waiter still owns the claimed buffer return.
            next = device.ReadAtAsync(4096, new byte[53]).AsTask();
            pool.Snapshot.Flights.ShouldBe(0);
            pool.Snapshot.ActiveHandles.ShouldBe(0);
            source.PayloadReads.ShouldBe(3);
            next.IsCompleted.ShouldBeFalse();
        }
        finally
        {
            source.Release();
            buffers.Release();
            if (next is not null)
                await next;
            await device.DisposeAsync();
        }
        cache.Snapshot.LiveRentedBytes.ShouldBe(0);
    }

    private sealed class GatedReturns(int blockReturn = 1) : ArrayPool<byte>
    {
        private readonly ArrayPool<byte> _inner = ArrayPool<byte>.Create();
        private readonly ManualResetEventSlim _released = new();
        private int _returns;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Release() => _released.Set();
        public override byte[] Rent(int minimumLength) => _inner.Rent(minimumLength);
        public override void Return(byte[] array, bool clearArray = false)
        {
            if (Interlocked.Increment(ref _returns) == blockReturn)
            {
                Entered.TrySetResult();
                if (!_released.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Synthetic return gate timed out.");
            }
            _inner.Return(array, clearArray);
        }
    }

    [Fact]
    public async Task ReadAtAsync_PreCancelledAdjacentCall_ResetsSequentialDetectionBeforeNextHealthyRead()
    {
        var fixture = SyntheticChdFixture.Create();
        await using var pool = new ChdReadPool(new MediaBlockCache(8192), 2, 0);
        await using var device = await Open(fixture.Source(), pool);
        await device.ReadAtAsync(0, new byte[2048]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() =>
            device.ReadAtAsync(2048, new byte[2048], cancellation.Token).AsTask());
        await device.ReadAtAsync(2048, new byte[2048]);
        pool.Snapshot.SpeculativeStarted.ShouldBe(0);
    }

    private static async Task Sequence(ChdBlockDevice device)
    {
        await device.ReadAtAsync(0, new byte[2048]);
        await device.ReadAtAsync(2048, new byte[2048]);
    }

    internal static ValueTask<ChdBlockDevice> Open(CountedRawContent source, ChdReadPool pool,
        string? identity = null, TimeSpan? timeout = null, CancellationToken ct = default) =>
        ChdBlockDevice.OpenAsync(source, pool, SyntheticChdFixture.Limits, identity, timeout, ct);
}
