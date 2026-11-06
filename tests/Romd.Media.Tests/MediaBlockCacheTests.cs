using Romd.Media.Rvz;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests;

public sealed class MediaBlockCacheTests
{
    [Fact]
    public async Task ReadAt_SecondImageWithSameIdentity_ServesCachedBlocksWithoutReadingGroups()
    {
        var disc = Discs.Wii();
        var cache = new MediaBlockCache(32L << 20);
        await using (var first = await Discs.OpenAsync(disc.File, cache, "artifact"))
            await first.ReadAtAsync(0x100000, new byte[0x200000]);
        var content = new MemoryContent(disc.File);
        await using var second = await RvzImage.OpenAsync(content, new RvzReadOptions(), cache, "artifact");
        int readsAfterOpen = content.Reads;

        byte[] actual = new byte[0x200000];
        await second.ReadAtAsync(0x100000, actual);

        content.Reads.ShouldBe(readsAfterOpen);
        actual.ShouldBe(disc.ExpectedDisc.AsSpan(0x100000, 0x200000).ToArray());
    }

    [Fact]
    public async Task ReadAt_ConcurrentReadsOfOneWiiRegionAcrossImages_DecodeItOnce()
    {
        var disc = Discs.Wii();
        int oneDecodeReads = await OneRegionDecodeReadsAsync(disc.File);
        var gate = new ReadGate();
        var firstContent = gate.Content(disc.File);
        var secondContent = gate.Content(disc.File);
        var cache = new MediaBlockCache(32L << 20);
        await using var first = await RvzImage.OpenAsync(firstContent, new RvzReadOptions(), cache, "region");
        await using var second = await RvzImage.OpenAsync(secondContent, new RvzReadOptions(), cache, "region");
        int readsAfterOpen = firstContent.Reads + secondContent.Reads;
        gate.Arm();
        byte[][] results = Enumerable.Range(0, 8).Select(_ => new byte[4096]).ToArray();

        Task<int>[] reads = results.Select((bytes, index) =>
            (index % 2 == 0 ? first : second).ReadAtAsync(0x100000, bytes).AsTask()).ToArray();
        gate.Open();

        (await Task.WhenAll(reads)).ShouldAllBe(count => count == 4096);
        foreach (byte[] bytes in results)
            bytes.ShouldBe(disc.ExpectedDisc.AsSpan(0x100000, 4096).ToArray());
        (firstContent.Reads + secondContent.Reads - readsAfterOpen).ShouldBe(oneDecodeReads);
    }

    [Theory]
    [InlineData("first", "second")]
    [InlineData(null, null)]
    public async Task ReadAt_ConcurrentReadsAcrossDistinctCacheIdentities_DecodeSeparately(string? firstIdentity, string? secondIdentity)
    {
        var disc = Discs.Wii();
        int oneDecodeReads = await OneRegionDecodeReadsAsync(disc.File);
        var gate = new ReadGate();
        var firstContent = gate.Content(disc.File);
        var secondContent = gate.Content(disc.File);
        var cache = new MediaBlockCache(32L << 20);
        await using var first = await RvzImage.OpenAsync(firstContent, new RvzReadOptions(), cache, firstIdentity);
        await using var second = await RvzImage.OpenAsync(secondContent, new RvzReadOptions(), cache, secondIdentity);
        int firstAfterOpen = firstContent.Reads, secondAfterOpen = secondContent.Reads;
        gate.Arm();
        byte[] firstBytes = new byte[4096], secondBytes = new byte[4096];

        Task<int> firstRead = first.ReadAtAsync(0x100000, firstBytes).AsTask();
        Task<int> secondRead = second.ReadAtAsync(0x100000, secondBytes).AsTask();
        gate.Open();
        await Task.WhenAll(firstRead, secondRead);

        (firstContent.Reads - firstAfterOpen).ShouldBe(oneDecodeReads);
        (secondContent.Reads - secondAfterOpen).ShouldBe(oneDecodeReads);
        firstBytes.ShouldBe(disc.ExpectedDisc.AsSpan(0x100000, 4096).ToArray());
        secondBytes.ShouldBe(firstBytes);
    }

    [Fact]
    public async Task DisposeAsync_PrivateImage_EvictsItsBlocks()
    {
        var cache = new MediaBlockCache(32L << 20);
        var image = await Discs.OpenAsync(Discs.Wii().File, cache);
        await Discs.ReadAllAsync(image);
        cache.UsedBytes.ShouldBeGreaterThan(0);

        await image.DisposeAsync();

        cache.UsedBytes.ShouldBe(0);
    }

    [Fact]
    public async Task ReadAt_WholeDisc_StaysWithinTheByteBudget()
    {
        var disc = Discs.Wii();
        var cache = new MediaBlockCache(3L << 20);
        await using var image = await Discs.OpenAsync(disc.File, cache, "budget");

        (await Discs.ReadAllAsync(image, bufferSize: 300_000)).ShouldBe(disc.ExpectedDisc);

        cache.UsedBytes.ShouldBeLessThanOrEqualTo(cache.BudgetBytes);
    }

    [Fact]
    public async Task ReadAt_ZeroBudget_StillReturnsCorrectBytes()
    {
        var disc = Discs.GameCube();
        await using var image = await Discs.OpenAsync(disc.File, new MediaBlockCache(0));

        (await Discs.ReadAllAsync(image)).ShouldBe(disc.ExpectedDisc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAt_ConcurrentOverlappingReadsUnderEviction_ReturnCorrectBytes(bool parallelRegions)
    {
        var disc = Discs.Wii();
        var cache = new MediaBlockCache(2L << 20); // one region: every read evicts blocks other readers hold
        await using var image = await Discs.OpenAsync(disc.File, cache, "shared",
            new RvzReadOptions { ParallelRegionDecode = parallelRegions });
        int mismatches = 0;

        await Task.WhenAll(Enumerable.Range(0, 8).Select(task => Task.Run(async () =>
        {
            var random = new Random(task);
            byte[] actual = new byte[1 << 20];
            for (int i = 0; i < 60; i++)
            {
                long offset = random.Next(2) == 0 ? 0x400000 + random.Next(0x80000) : random.NextInt64(0, disc.ExpectedDisc.Length);
                int size = (int)Math.Min(random.Next(1, actual.Length), disc.ExpectedDisc.Length - offset);
                int read = await image.ReadAtAsync(offset, actual.AsMemory(0, size));
                if (read != size || !actual.AsSpan(0, size).SequenceEqual(disc.ExpectedDisc.AsSpan((int)offset, size)))
                    Interlocked.Increment(ref mismatches);
            }
        })));

        mismatches.ShouldBe(0);
        cache.UsedBytes.ShouldBeLessThanOrEqualTo(cache.BudgetBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1 << 20)]
    public async Task GetOrAddAsync_ConcurrentMisses_ShareOneFactoryCall(long budget)
    {
        var cache = new MediaBlockCache(budget);
        var decode = new GatedDecode(cache);
        var key = new BlockKey("owner", 0, 7);

        Task<Block>[] waiters = Enumerable.Range(0, 8).Select(_ => decode.GetAsync(key, CancellationToken.None)).ToArray();
        cache.FlightWaiters("owner").ShouldBe(8);
        decode.Release();
        Block[] blocks = await Task.WhenAll(waiters);

        decode.Calls.ShouldBe(1);
        blocks.ShouldAllBe(block => ReferenceEquals(block, blocks[0]));
        cache.FlightWaiters("owner").ShouldBe(0);
        foreach (Block block in blocks)
            cache.Release(block);
        blocks[0].References.ShouldBe(budget == 0 ? 0 : 1); // Only a retained block keeps the cache's reference.
    }

    [Fact]
    public async Task GetOrAddAsync_CancelledWaiter_DoesNotCancelSharedFactory()
    {
        var cache = new MediaBlockCache(1 << 20);
        var decode = new GatedDecode(cache);
        var key = new BlockKey("owner", 0, 0);
        using var cancellation = new CancellationTokenSource();
        Task<Block> cancelled = decode.GetAsync(key, cancellation.Token);
        Task<Block> healthy = decode.GetAsync(key, CancellationToken.None);

        await cancellation.CancelAsync();
        var failure = await Should.ThrowAsync<OperationCanceledException>(() => cancelled);
        failure.CancellationToken.ShouldBe(cancellation.Token);
        healthy.IsCompleted.ShouldBeFalse();
        cache.FlightWaiters("owner").ShouldBe(1);
        decode.Release();
        Block block = await healthy;

        decode.Calls.ShouldBe(1);
        block.Buffer[0].ShouldBe(GatedDecode.Marker);
        cache.Release(block);
    }

    [Fact]
    public async Task GetOrAddAsync_EveryWaiterCancelled_FactoryStillCompletesIntoCache()
    {
        var cache = new MediaBlockCache(1 << 20);
        var decode = new GatedDecode(cache);
        var key = new BlockKey("owner", 0, 0);
        using var cancellation = new CancellationTokenSource();
        Task<Block> cancelled = decode.GetAsync(key, cancellation.Token);

        await cancellation.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => cancelled);
        decode.Release();
        await WaitFor(() => cache.UsedBytes > 0);
        Block block = await decode.GetAsync(key, CancellationToken.None);

        decode.Calls.ShouldBe(1);
        await WaitFor(() => Volatile.Read(ref block.References) == 2); // The cache's and this caller's, once the flight lets go of its own.
        cache.Release(block);
    }

    [Fact]
    public async Task GetOrAddAsync_FaultedFactory_FailsEveryWaiterAndRetriesOnNextCall()
    {
        var cache = new MediaBlockCache(1 << 20);
        var decode = new GatedDecode(cache) { Failures = 1 };
        var key = new BlockKey("owner", 0, 0);
        Task<Block> first = decode.GetAsync(key, CancellationToken.None);
        Task<Block> second = decode.GetAsync(key, CancellationToken.None);

        decode.Release();
        (await Should.ThrowAsync<IOException>(() => first)).Message.ShouldBe(GatedDecode.FailureMessage);
        (await Should.ThrowAsync<IOException>(() => second)).Message.ShouldBe(GatedDecode.FailureMessage);
        cache.UsedBytes.ShouldBe(0);
        cache.FlightWaiters("owner").ShouldBe(0);
        Block block = await decode.GetAsync(key, CancellationToken.None);

        decode.Calls.ShouldBe(2);
        block.Buffer[0].ShouldBe(GatedDecode.Marker);
        cache.Release(block);
    }

    [Fact]
    public async Task GetOrAddAsync_FactoryCompletesWithoutBlock_FaultsEveryWaiterAndCachesNothing()
    {
        var cache = new MediaBlockCache(1 << 20);
        var key = new BlockKey("owner", 0, 0);
        var gate = new TaskCompletionSource<Block>(TaskCreationOptions.RunContinuationsAsynchronously);
        ValueTask<Block> first = cache.GetOrAddAsync(key, gate, static pending => pending.Task, CancellationToken.None);
        ValueTask<Block> second = cache.GetOrAddAsync(key, gate, static pending => pending.Task, CancellationToken.None);

        gate.SetResult(null!);
        await Should.ThrowAsync<InvalidOperationException>(first.AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        await Should.ThrowAsync<InvalidOperationException>(second.AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        cache.UsedBytes.ShouldBe(0);
        cache.FlightWaiters("owner").ShouldBe(0);
    }

    [Fact]
    public async Task GetOrAddAsync_CompletionThrows_FaultsEveryWaiterInsteadOfHanging()
    {
        var cache = new MediaBlockCache(4096);
        // The pool rejects this buffer on return, so evicting it while admitting the decode throws inside completion.
        cache.Release(await cache.GetOrAddAsync(new BlockKey("foreign", 0, 0), 0,
            static _ => Task.FromResult(new Block(new byte[4000], 4000)), CancellationToken.None));
        var decode = new GatedDecode(cache);
        var key = new BlockKey("owner", 0, 0);
        Task<Block> first = decode.GetAsync(key, CancellationToken.None);
        Task<Block> second = decode.GetAsync(key, CancellationToken.None);

        decode.Release();
        await Should.ThrowAsync<ArgumentException>(() => first.WaitAsync(TimeSpan.FromSeconds(3)));
        await Should.ThrowAsync<ArgumentException>(() => second.WaitAsync(TimeSpan.FromSeconds(3)));

        cache.FlightWaiters("owner").ShouldBe(0);
        cache.UsedBytes.ShouldBe(4096); // The decoded block was admitted before the eviction failed.
        Block block = await decode.GetAsync(key, CancellationToken.None);
        decode.Calls.ShouldBe(1);
        cache.Release(block);
        block.References.ShouldBe(1); // Only the cache's own reference remains.
    }

    [Fact]
    public async Task GetOrAddAsync_OwnerEvictedDuringDecode_ServesWaitersWithoutRetaining()
    {
        var cache = new MediaBlockCache(1 << 20);
        var decode = new GatedDecode(cache);
        Task<Block> waiter = decode.GetAsync(new BlockKey("private", 0, 0), CancellationToken.None);

        cache.Evict("private");
        decode.Release();
        Block block = await waiter;

        block.Buffer[0].ShouldBe(GatedDecode.Marker);
        cache.UsedBytes.ShouldBe(0);
        cache.Release(block);
        block.References.ShouldBe(0);
    }

    [Fact]
    public async Task GetOrAddAsync_FactoryThrowsSynchronously_FailsEveryWaiterAndRetriesOnNextCall()
    {
        var cache = new MediaBlockCache(1 << 20);
        var key = new BlockKey("owner", 0, 0);
        using var entered = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        var failing = (Entered: entered, Proceed: proceed);
        Task<Block> starter = Task.Run(() => cache.GetOrAddAsync(key, failing, static state =>
        {
            state.Entered.Set();
            state.Proceed.Wait();
            throw new IOException(GatedDecode.FailureMessage);
        }, CancellationToken.None).AsTask());
        entered.Wait(TimeSpan.FromSeconds(3)).ShouldBeTrue();
        var joining = new GatedDecode(cache);
        Task<Block> joiner = joining.GetAsync(key, CancellationToken.None);
        cache.FlightWaiters("owner").ShouldBe(2);

        proceed.Set();
        (await Should.ThrowAsync<IOException>(() => starter)).Message.ShouldBe(GatedDecode.FailureMessage);
        (await Should.ThrowAsync<IOException>(() => joiner)).Message.ShouldBe(GatedDecode.FailureMessage);
        joining.Calls.ShouldBe(0);
        cache.UsedBytes.ShouldBe(0);
        cache.FlightWaiters("owner").ShouldBe(0);
        var retry = new GatedDecode(cache, gated: false);
        Block block = await retry.GetAsync(key, CancellationToken.None);

        retry.Calls.ShouldBe(1);
        block.Buffer[0].ShouldBe(GatedDecode.Marker);
        cache.Release(block);
    }

    [Fact]
    public async Task GetOrAddAsync_JoinedKeyDuringDecode_ServesSameBlockWithoutSecondFactory()
    {
        var cache = new MediaBlockCache(1 << 20);
        var decode = new GatedDecode(cache);
        var unused = new GatedDecode(cache, gated: false);
        var key = new BlockKey("owner", 0, 0);
        Task<Block> waiter = decode.GetAsync(key, CancellationToken.None);
        Task<Block> joiner = unused.GetAsync(key, CancellationToken.None);

        decode.Release();
        Block block = await waiter;
        Block joined = await joiner;

        joined.ShouldBeSameAs(block);
        unused.Calls.ShouldBe(0);
        block.References.ShouldBe(3); // Two callers and the cache.
        cache.Snapshot.NewFactories.ShouldBe(1);
        cache.Snapshot.FlightJoins.ShouldBe(1);
        cache.Release(block);
        cache.Release(joined);
        block.References.ShouldBe(1);
    }

    [Fact]
    public async Task GetOrAddAsync_Hit_DoesNotAllocate()
    {
        var cache = new MediaBlockCache(1 << 20);
        var key = new BlockKey("owner", 0, 0);
        cache.Release(await new GatedDecode(cache, gated: false).GetAsync(key, CancellationToken.None));
        for (int warmUp = 0; warmUp < 100; warmUp++)
            cache.Release(Hit(cache, key));

        long before = GC.GetAllocatedBytesForCurrentThread();
        Block block = Hit(cache, key);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        cache.Release(block);
        allocated.ShouldBe(0);
    }

    // A value-tuple state, as the RVZ and CHD callers pass, with a static factory.
    private static Block Hit(MediaBlockCache cache, BlockKey key)
    {
        ValueTask<Block> pending = cache.GetOrAddAsync(key, (Cache: cache, Index: key.Index),
            static _ => throw new InvalidOperationException("A cache hit must not decode."), CancellationToken.None);
        return pending.IsCompletedSuccessfully ? pending.Result : throw new InvalidOperationException("A cache hit must complete synchronously.");
    }

    private static async Task<int> OneRegionDecodeReadsAsync(byte[] file)
    {
        var content = new MemoryContent(file);
        await using var image = await RvzImage.OpenAsync(content, new RvzReadOptions(), new MediaBlockCache(32L << 20));
        int readsAfterOpen = content.Reads;
        await image.ReadAtAsync(0x100000, new byte[4096]);
        int reads = content.Reads - readsAfterOpen;
        reads.ShouldBeGreaterThan(0);
        return reads;
    }

    private static async Task WaitFor(Func<bool> predicate)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(3))
                throw new TimeoutException("Expected cache state was not reached.");
            await Task.Delay(5);
        }
    }

    /// <summary>A factory that counts calls and holds every decode until released.</summary>
    private sealed class GatedDecode(MediaBlockCache cache, bool gated = true)
    {
        public const byte Marker = 0x5A;
        public const string FailureMessage = "Synthetic decode failure";
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);
        public int Failures { get; init; }

        public Task<Block> GetAsync(BlockKey key, CancellationToken ct) =>
            cache.GetOrAddAsync(key, this, static decode => decode.DecodeAsync(), ct).AsTask();

        public void Release() => _gate.SetResult();

        private async Task<Block> DecodeAsync()
        {
            int call = Interlocked.Increment(ref _calls);
            if (gated)
                await _gate.Task;
            if (call <= Failures)
                throw new IOException(FailureMessage);
            byte[] buffer = cache.Pool.Rent(4096);
            buffer[0] = Marker;
            return new Block(buffer, 4096);
        }
    }

    /// <summary>Holds every artifact read of the contents it creates, once armed, until opened.</summary>
    private sealed class ReadGate
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _armed;

        public MemoryContent Content(byte[] file) =>
            new(file) { BeforeRead = ct => _armed ? _gate.Task.WaitAsync(ct) : Task.CompletedTask };

        public void Arm() => _armed = true;

        public void Open() => _gate.SetResult();
    }
}
