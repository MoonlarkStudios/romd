using Romd.Media.TestSupport;
using System.Diagnostics;
using Romd.Media.PlayStation;
using Shouldly;
using Xunit;
using static Romd.Media.Tests.PlayStation.ChdBlockDeviceTests;

namespace Romd.Media.Tests.PlayStation;

public sealed class ChdReadPoolTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8192)]
    public async Task ReadAtAsync_ConcurrentSameArtifactHunk_SingleDecodeSurvivesUnretainedBuffers(long budget)
    {
        var fixture = SyntheticChdFixture.Create();
        var source = fixture.Source();
        var duplicate = fixture.Source();
        await using var pool = new ChdReadPool(new MediaBlockCache(budget), 2, 1);
        await using var first = await Open(source, pool, "shared");
        await using var second = await Open(duplicate, pool, "shared");
        duplicate.DisposeCalls.ShouldBe(1);
        source.Arm();
        byte[][] results = Enumerable.Range(0, 8).Select(_ => new byte[53]).ToArray();
        Task<int>[] reads = results.Select((bytes, index) =>
            (index % 2 == 0 ? first : second).ReadAtAsync(11, bytes).AsTask()).ToArray();
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await WaitFor(() => pool.Snapshot.FlightWaiters == 8);
            source.PayloadReads.ShouldBe(1);
            pool.Snapshot.Flights.ShouldBe(1);
            pool.Snapshot.ActiveHandles.ShouldBe(1);
        }
        finally { source.Release(); }
        (await Task.WhenAll(reads)).ShouldAllBe(count => count == 53);
        foreach (byte[] bytes in results)
            bytes.ShouldBe(fixture.Expected(11, bytes.Length));
        source.PayloadReads.ShouldBe(1);
        pool.Snapshot.Flights.ShouldBe(0);
    }

    [Fact]
    public async Task ReadAtAsync_ReopenedTrustedIdentity_ReusesSharedCacheWithoutRetainingSource()
    {
        var fixture = SyntheticChdFixture.Create();
        var cache = new MediaBlockCache(4096);
        await using var pool = new ChdReadPool(cache, 1, 1);
        var firstSource = fixture.Source();
        await using (var first = await Open(firstSource, pool, "immutable"))
            await first.ReadAtAsync(19, new byte[53]);
        firstSource.DisposeCalls.ShouldBe(1);
        var secondSource = fixture.Source();
        await using var second = await Open(secondSource, pool, "immutable");
        secondSource.Arm(block: false);
        byte[] bytes = new byte[53];

        await second.ReadAtAsync(19, bytes);

        bytes.ShouldBe(fixture.Expected(19, bytes.Length));
        secondSource.PayloadReads.ShouldBe(0);
        cache.UsedBytes.ShouldBeLessThanOrEqualTo(cache.BudgetBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAtAsync_DifferentArtifactsWithSameHunkIndex_DoNotAlias(bool privateIdentity)
    {
        var firstFixture = SyntheticChdFixture.Create(seed: 7);
        var secondFixture = SyntheticChdFixture.Create(seed: 199);
        var cache = new MediaBlockCache(16384);
        await using var pool = new ChdReadPool(cache, 2, 1);
        await using var first = await Open(firstFixture.Source(), pool, privateIdentity ? null : "first");
        await using var second = await Open(secondFixture.Source(), pool, privateIdentity ? null : "second");
        byte[] firstBytes = new byte[53], secondBytes = new byte[53];

        await first.ReadAtAsync(11, firstBytes);
        await second.ReadAtAsync(11, secondBytes);

        firstBytes.ShouldBe(firstFixture.Expected(11, firstBytes.Length));
        secondBytes.ShouldBe(secondFixture.Expected(11, secondBytes.Length));
        secondBytes.ShouldNotBe(firstBytes);
    }

    [Fact]
    public async Task ReadAtAsync_CacheKeyFromOtherMedia_CannotAliasChdHunk()
    {
        var fixture = SyntheticChdFixture.Create();
        var cache = new MediaBlockCache(8192);
        byte[] poisoned = cache.Pool.Rent(4096);
        poisoned.AsSpan().Fill(0xFE);
        cache.Release(await cache.GetOrAddAsync(new BlockKey("same-identity", 0, 0), poisoned,
            static bytes => Task.FromResult(new Block(bytes, 4096)), CancellationToken.None));
        await using var pool = new ChdReadPool(cache, 1, 1);
        await using var device = await Open(fixture.Source(), pool, "same-identity");
        byte[] actual = new byte[53];

        await device.ReadAtAsync(11, actual);

        actual.ShouldBe(fixture.Expected(11, actual.Length));
    }

    [Fact]
    public async Task OpenAsync_SameIdentityDifferentGeometry_DisposesIncomingSourceAndKeepsOriginal()
    {
        var fixture = SyntheticChdFixture.Create();
        var source = fixture.Source();
        var changed = SyntheticChdFixture.Create(finalBytes: 2048).Source();
        await using var pool = new ChdReadPool(new MediaBlockCache(0), 1, 1);
        await using var first = await Open(source, pool, "geometry");

        await Should.ThrowAsync<InvalidDataException>(() => Open(changed, pool, "geometry").AsTask());

        changed.DisposeCalls.ShouldBe(1);
        source.DisposeCalls.ShouldBe(0);
        byte[] bytes = new byte[53];
        await first.ReadAtAsync(0, bytes);
        bytes.ShouldBe(fixture.Expected(0, bytes.Length));
    }

    [Fact]
    public async Task OpenAsync_InvalidCompressedMap_ReleasesSourceAndAdmissionForHealthyOpen()
    {
        var broken = SyntheticChdFixture.Create();
        broken.Bytes[134] ^= 0xFF; // Map CRC; the admitted fixed header remains well formed.
        var source = broken.Source();
        await using var pool = new ChdReadPool(new MediaBlockCache(0), 1, 1);

        await Should.ThrowAsync<InvalidDataException>(() => Open(source, pool).AsTask());

        source.DisposeCalls.ShouldBe(1);
        pool.Snapshot.LiveHandles.ShouldBe(0);
        var healthy = SyntheticChdFixture.Create();
        await using var device = await Open(healthy.Source(), pool);
        byte[] bytes = new byte[53];
        await device.ReadAtAsync(0, bytes);
        bytes.ShouldBe(healthy.Expected(0, bytes.Length));
    }

    [Fact]
    public async Task ReadAtAsync_DistinctHunks_BorrowExclusiveHandlesWithinGlobalBounds()
    {
        var fixture = SyntheticChdFixture.Create();
        var source = fixture.Source();
        await using var pool = new ChdReadPool(new MediaBlockCache(0), 2, 1);
        await using var parked = await Open(fixture.Source(), pool, "parked-artifact");
        await using var device = await Open(source, pool);
        source.Arm();
        byte[] first = new byte[53], second = new byte[53];
        Task<int> firstRead = device.ReadAtAsync(0, first).AsTask();
        Task<int> secondRead = device.ReadAtAsync(fixture.HunkBytes, second).AsTask();
        try
        {
            await WaitFor(() => source.PayloadReads == 2);
            pool.Snapshot.ActiveHandles.ShouldBe(2);
            source.PeakActive.ShouldBe(2);
            pool.Snapshot.PeakLiveHandles.ShouldBeLessThanOrEqualTo(2);
        }
        finally { source.Release(); }
        await Task.WhenAll(firstRead, secondRead);
        first.ShouldBe(fixture.Expected(0, 53));
        second.ShouldBe(fixture.Expected(fixture.HunkBytes, 53));
        pool.Snapshot.IdleHandles.ShouldBe(1);
        pool.Snapshot.ActiveHandles.ShouldBe(0);
    }

    [Fact]
    public async Task OpenAsync_ManyLiveArtifacts_IdleBoundIsGlobalAndSourcesRetire()
    {
        var fixture = SyntheticChdFixture.Create();
        await using var pool = new ChdReadPool(new MediaBlockCache(0), 2, 1);
        var sources = Enumerable.Range(0, 12).Select(_ => fixture.Source()).ToArray();
        var devices = new List<ChdBlockDevice>();
        try
        {
            for (int index = 0; index < sources.Length; index++)
            {
                devices.Add(await Open(sources[index], pool, $"artifact-{index}"));
                pool.Snapshot.IdleHandles.ShouldBeLessThanOrEqualTo(1);
                pool.Snapshot.LiveHandles.ShouldBeLessThanOrEqualTo(2);
            }
            pool.Snapshot.PeakLiveHandles.ShouldBeLessThanOrEqualTo(2);
            pool.Snapshot.PeakActiveHandles.ShouldBeLessThanOrEqualTo(2);
        }
        finally { foreach (var device in devices) await device.DisposeAsync(); }
        sources.ShouldAllBe(source => source.DisposeCalls == 1);
        pool.Snapshot.LiveHandles.ShouldBe(0);
        pool.Snapshot.Artifacts.ShouldBe(0);
        pool.Snapshot.OpenedHandles.ShouldBe(pool.Snapshot.ClosedHandles);
    }

    [Fact]
    public async Task ReadAtAsync_CancelledWaiter_DoesNotCancelSharedDecode()
    {
        var fixture = SyntheticChdFixture.Create();
        var source = fixture.Source();
        await using var pool = new ChdReadPool(new MediaBlockCache(0), 1, 1);
        await using var device = await Open(source, pool);
        source.Arm();
        byte[] good = new byte[53];
        using var cancellation = new CancellationTokenSource();
        Task<int> cancelled = device.ReadAtAsync(0, new byte[53], cancellation.Token).AsTask();
        Task<int> healthy = device.ReadAtAsync(0, good).AsTask();
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await WaitFor(() => pool.Snapshot.FlightWaiters == 2);
            cancellation.Cancel();
            OperationCanceledException? failure = null;
            try
            { await cancelled; }
            catch (OperationCanceledException error) { failure = error; }
            failure.ShouldNotBeNull();
            failure.CancellationToken.ShouldBe(cancellation.Token);
            healthy.IsCompleted.ShouldBeFalse();
            source.PayloadReads.ShouldBe(1);
        }
        finally { source.Release(); }
        await healthy;
        good.ShouldBe(fixture.Expected(0, good.Length));
        pool.Snapshot.Flights.ShouldBe(0);
    }

    [Fact]
    public async Task ReadAtAsync_FailedSharedDecode_DiscardsHandleAndAllowsHealthyRetry()
    {
        var fixture = SyntheticChdFixture.Create();
        var source = fixture.Source();
        var cache = new MediaBlockCache(8192);
        await using var pool = new ChdReadPool(cache, 1, 1);
        await using var device = await Open(source, pool);
        source.Arm(failures: 1);
        Task<int> first = device.ReadAtAsync(0, new byte[53]).AsTask();
        Task<int> second = device.ReadAtAsync(0, new byte[53]).AsTask();
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await WaitFor(() => pool.Snapshot.FlightWaiters == 2);
        }
        finally { source.Release(); }
        (await Should.ThrowAsync<IOException>(() => first)).Message.ShouldContain("Synthetic payload read failure");
        (await Should.ThrowAsync<IOException>(() => second)).Message.ShouldContain("Synthetic payload read failure");
        cache.UsedBytes.ShouldBe(0);
        pool.Snapshot.LiveHandles.ShouldBe(0);
        pool.Snapshot.Flights.ShouldBe(0);
        source.Arm(block: false);
        byte[] good = new byte[53];

        await device.ReadAtAsync(0, good);

        good.ShouldBe(fixture.Expected(0, good.Length));
        pool.Snapshot.IdleHandles.ShouldBe(1);
        pool.Snapshot.OpenedHandles.ShouldBe(2);
    }

    [Fact]
    public async Task ReadAtAsync_AdmissionTimeout_ReleasesFlightAndCapacity()
    {
        var fixture = SyntheticChdFixture.Create();
        var source = fixture.Source();
        await using var pool = new ChdReadPool(new MediaBlockCache(0), 1, 1);
        await using var first = await Open(source, pool);
        await using var second = await Open(fixture.Source(), pool, timeout: TimeSpan.FromMilliseconds(25));
        source.Arm();
        Task<int> blocked = first.ReadAtAsync(0, new byte[53]).AsTask();
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Should.ThrowAsync<TimeoutException>(() => second.ReadAtAsync(0, new byte[53]).AsTask());
        }
        finally { source.Release(); }
        await blocked;
        byte[] good = new byte[53];
        await second.ReadAtAsync(0, good);
        good.ShouldBe(fixture.Expected(0, good.Length));
        pool.Snapshot.Flights.ShouldBe(0);
        pool.Snapshot.ActiveHandles.ShouldBe(0);
    }

    [Fact]
    public async Task DisposeAsync_LastWaiterCancelled_JoinsOwnedDecodeBeforeClosingSource()
    {
        var fixture = SyntheticChdFixture.Create();
        var source = fixture.Source();
        var pool = new ChdReadPool(new MediaBlockCache(0), 1, 1);
        var device = await Open(source, pool);
        using var cancellation = new CancellationTokenSource();
        source.Arm();
        Task<int> read = device.ReadAtAsync(0, new byte[53], cancellation.Token).AsTask();
        Task? disposal = null;
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancellation.Cancel();
            try
            { await read; }
            catch (OperationCanceledException error) { error.CancellationToken.ShouldBe(cancellation.Token); }
            disposal = device.DisposeAsync().AsTask();
            disposal.IsCompleted.ShouldBeFalse();
            source.DisposeCalls.ShouldBe(0);
        }
        finally { source.Release(); }
        await (disposal ?? device.DisposeAsync().AsTask());
        await pool.DisposeAsync();
        source.DisposeCalls.ShouldBe(1);
        pool.Snapshot.LiveHandles.ShouldBe(0);
        pool.Snapshot.Flights.ShouldBe(0);
    }

    [Fact]
    public async Task ReadAtAsync_PrefetchPressure_ReservesZeroTimeoutDemandCapacityAndBoundsRoundedBuffers()
    {
        var fixture = SyntheticChdFixture.Create(hunks: 5);
        var speculative = fixture.Source();
        var demandSource = fixture.Source();
        var cache = new MediaBlockCache(0);
        await using var pool = new ChdReadPool(cache, 2, 0);
        await using var first = await Open(speculative, pool, "scan");
        await using var second = await Open(demandSource, pool, "other-artifact");
        // Budget zero removes retained-cache effects. Both sources gate only their next hunk.
        speculative.Arm(blockAtHunk: 1);
        demandSource.Arm(blockAtHunk: 1);
        Task<int>? demand = null;
        try
        {
            await first.ReadAtAsync(0, new byte[2048]);
            await first.ReadAtAsync(2048, new byte[2048]);
            await speculative.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            demand = second.ReadAtAsync(4096, new byte[53]).AsTask();
            await demandSource.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            // The zero-timeout demand borrowed the one reserved non-speculative permit.
            pool.Snapshot.ActiveHandles.ShouldBe(2);
            pool.Snapshot.OpeningHandles.ShouldBe(0);
            pool.Snapshot.PeakOpeningHandles.ShouldBeLessThanOrEqualTo(2);
            pool.Snapshot.Flights.ShouldBe(2);
            pool.Snapshot.Speculations.ShouldBe(1);
            pool.Snapshot.PeakSpeculations.ShouldBe(1);
            pool.Snapshot.PeakLiveHandles.ShouldBeLessThanOrEqualTo(2);
            cache.Snapshot.LiveRentedBytes.ShouldBe(8192);
            cache.Snapshot.PeakRentedBytes.ShouldBeLessThanOrEqualTo(8192);
            cache.UsedBytes.ShouldBe(0);
            // A second sequential reader cannot enqueue more speculation globally.
            demandSource.Release();
            await demand;
            await second.ReadAtAsync(4096 + 53, new byte[53]);
            pool.Snapshot.SpeculativeStarted.ShouldBe(1);
        }
        finally
        {
            speculative.Release();
            demandSource.Release();
            if (demand is not null)
                await demand;
        }
        await first.DisposeAsync();
        await second.DisposeAsync();
        cache.Snapshot.LiveRentedBytes.ShouldBe(0);
        pool.Snapshot.LiveHandles.ShouldBe(0);
        pool.Snapshot.Speculations.ShouldBe(0);
    }

    [Fact]
    public async Task DisposeAsync_CancelledOrigin_SharedHealthyReaderKeepsSingleFlightAndItsOwnAdmission()
    {
        var fixture = SyntheticChdFixture.Create();
        var source = fixture.Source();
        await using var pool = new ChdReadPool(new MediaBlockCache(0), 2, 0);
        var origin = await Open(source, pool, "shared");
        await using var healthy = await Open(fixture.Source(), pool, "shared");
        using var cancellation = new CancellationTokenSource();
        source.Arm();
        Task<int> cancelled = origin.ReadAtAsync(0, new byte[53], cancellation.Token).AsTask();
        byte[] bytes = new byte[53];
        Task<int> joined = healthy.ReadAtAsync(0, bytes).AsTask();
        Task? disposal = null;
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await WaitFor(() => pool.Snapshot.FlightWaiters == 2);
            cancellation.Cancel();
            await Should.ThrowAsync<OperationCanceledException>(() => cancelled);
            disposal = origin.DisposeAsync().AsTask();
            disposal.IsCompleted.ShouldBeFalse();
            joined.IsCompleted.ShouldBeFalse();
            source.PayloadReads.ShouldBe(1);
        }
        finally { source.Release(); await joined; await (disposal ?? origin.DisposeAsync().AsTask()); }
        bytes.ShouldBe(fixture.Expected(0, bytes.Length));
        source.DisposeCalls.ShouldBe(0); // The healthy device retains its shared source.
        await healthy.DisposeAsync();
        source.DisposeCalls.ShouldBe(1);
    }

    private static async Task WaitFor(Func<bool> predicate)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(3))
                throw new TimeoutException("Expected synthetic concurrency state was not reached.");
            await Task.Delay(5);
        }
    }
}
