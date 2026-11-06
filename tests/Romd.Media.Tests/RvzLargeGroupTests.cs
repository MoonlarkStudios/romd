using System.Buffers;
using Romd.Media.Rvz;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests;

public sealed class RvzLargeGroupTests
{
    private const int ChunkSize = 8 << 20, RegionSize = 2 << 20, PartitionStart = 10 * ReferenceDisc.SectorSize;

    [Theory]
    [InlineData(RvzMethod.Zstd)]
    [InlineData(RvzMethod.None)]
    [InlineData(RvzMethod.Bzip2)]
    [InlineData(RvzMethod.Lzma)]
    [InlineData(RvzMethod.Lzma2)]
    public async Task ReadAt_DifferentRegionsOfLargeWiiGroup_ReadsAndDecodesGroupOnce(RvzMethod method)
    {
        var disc = LargeDisc(method: method);
        var content = new MemoryContent(disc.File);
        var cache = new MediaBlockCache(64L << 20);
        await using var image = await RvzImage.OpenAsync(content, new RvzReadOptions(), cache);
        int readsAfterOpen = content.Reads;

        for (int region = 0; region < 4; region++)
            await AssertRegionReadAsync(image, disc, region);

        (content.Reads - readsAfterOpen).ShouldBe(1);
        cache.Snapshot.NewFactories.ShouldBe(5); // Four reconstructed regions share one source-group decode.
        cache.UsedBytes.ShouldBeLessThanOrEqualTo(cache.BudgetBytes);
    }

    [Theory]
    [InlineData(145, RvzMethod.Zstd)]
    [InlineData(145, RvzMethod.None)]
    [InlineData(272, RvzMethod.Zstd)]
    [InlineData(272, RvzMethod.None)]
    public async Task ReadAt_PartialLargeWiiGroupWithPackedJunkAndExceptions_MatchesWholeDisc(int sectors, RvzMethod method)
    {
        var disc = LargeDisc(sectors, method);
        await using var image = await Discs.OpenAsync(disc.File, new MediaBlockCache(64L << 20));

        (await Discs.ReadAllAsync(image, 777_777)).ShouldBe(disc.ExpectedDisc);
    }

    [Fact]
    public async Task ReadAt_ZeroStoredLargeWiiGroup_ReusesClearedPayloadAcrossRegions()
    {
        var disc = LargeDisc(zeroPayload: true);
        var content = new MemoryContent(disc.File);
        var cache = new MediaBlockCache(64L << 20);
        await using var image = await RvzImage.OpenAsync(content, new RvzReadOptions(), cache);
        int readsAfterOpen = content.Reads;

        for (int region = 0; region < 4; region++)
            await AssertRegionReadAsync(image, disc, region);

        content.Reads.ShouldBe(readsAfterOpen); // Stored-size-zero groups have no artifact payload.
        cache.Snapshot.NewFactories.ShouldBe(5);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadAt_ConcurrentDifferentRegionsAcrossSharedImages_JoinOneSourceGroup(bool sharedImages)
    {
        var disc = LargeDisc();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool armed = false;
        Func<CancellationToken, Task> beforeRead = ct => armed ? gate.Task.WaitAsync(ct) : Task.CompletedTask;
        var firstContent = new MemoryContent(disc.File) { BeforeRead = beforeRead };
        var secondContent = new MemoryContent(disc.File) { BeforeRead = beforeRead };
        var cache = new MediaBlockCache(64L << 20);
        await using var first = await RvzImage.OpenAsync(firstContent, new RvzReadOptions(), cache, "large-group");
        await using var second = await RvzImage.OpenAsync(secondContent, new RvzReadOptions(), cache, "large-group");
        int readsAfterOpen = firstContent.Reads + secondContent.Reads;
        armed = true;
        Task[] reads = Enumerable.Range(0, 4).Select(region =>
            AssertRegionReadAsync(sharedImages && region % 2 == 1 ? second : first, disc, region)).ToArray();
        try
        {
            reads.ShouldAllBe(read => !read.IsCompleted);
            cache.Snapshot.FlightJoins.ShouldBe(3);
        }
        finally { gate.SetResult(); }

        await Task.WhenAll(reads).WaitAsync(TimeSpan.FromSeconds(10));
        (firstContent.Reads + secondContent.Reads - readsAfterOpen).ShouldBe(1);
    }

    [Fact]
    public async Task DisposeAsync_CancelledOriginWithDifferentRegionJoin_WaitsForSharedSourceGroup()
    {
        var disc = LargeDisc();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool armed = false;
        var originContent = new MemoryContent(disc.File)
        {
            BeforeRead = ct => armed ? gate.Task.WaitAsync(ct) : Task.CompletedTask
        };
        var joiningContent = new MemoryContent(disc.File);
        var cache = new MediaBlockCache(64L << 20);
        await using var origin = await RvzImage.OpenAsync(originContent, new RvzReadOptions(), cache, "large-group");
        await using var joiner = await RvzImage.OpenAsync(joiningContent, new RvzReadOptions(), cache, "large-group");
        int readsAfterOpen = originContent.Reads + joiningContent.Reads;
        armed = true;
        using var cancellation = new CancellationTokenSource();
        Task<int> cancelled = origin.ReadAtAsync(PartitionStart, new byte[4096], cancellation.Token).AsTask();
        Task healthy = AssertRegionReadAsync(joiner, disc, 1);
        Task disposal;
        try
        {
            await cancellation.CancelAsync();
            (await Should.ThrowAsync<OperationCanceledException>(() => cancelled)).CancellationToken.ShouldBe(cancellation.Token);
            disposal = origin.DisposeAsync().AsTask();
            disposal.IsCompleted.ShouldBeFalse();
            originContent.Disposed.ShouldBeFalse();
            healthy.IsCompleted.ShouldBeFalse();
        }
        finally { gate.SetResult(); }

        await Task.WhenAll(healthy, disposal).WaitAsync(TimeSpan.FromSeconds(10));
        originContent.Disposed.ShouldBeTrue();
        (originContent.Reads + joiningContent.Reads - readsAfterOpen).ShouldBe(1);
    }

    [Fact]
    public async Task ReadAt_SharedSourceGroupFails_ReleasesBuffersAndAllowsRetry()
    {
        var disc = LargeDisc();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool armed = false, fail = true;
        var content = new MemoryContent(disc.File)
        {
            BeforeRead = async ct =>
            {
                if (!armed)
                    return;
                await gate.Task.WaitAsync(ct);
                if (fail)
                    throw new IOException("Synthetic source-group failure.");
            }
        };
        var cache = new MediaBlockCache(64L << 20);
        await using var image = await RvzImage.OpenAsync(content, new RvzReadOptions(), cache);
        int readsAfterOpen = content.Reads;
        armed = true;
        Task first = AssertRegionReadAsync(image, disc, 0);
        Task second = AssertRegionReadAsync(image, disc, 1);
        gate.SetResult();

        (await Should.ThrowAsync<IOException>(() => first)).Message.ShouldBe("Synthetic source-group failure.");
        (await Should.ThrowAsync<IOException>(() => second)).Message.ShouldBe("Synthetic source-group failure.");
        cache.UsedBytes.ShouldBe(0);
        cache.Snapshot.LiveRentedBytes.ShouldBe(0);
        cache.Snapshot.Flights.ShouldBe(0);
        fail = false;

        await AssertRegionReadAsync(image, disc, 0);
        await AssertRegionReadAsync(image, disc, 1);
        (content.Reads - readsAfterOpen).ShouldBe(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(RegionSize)]
    public async Task ReadAt_LargeSourceGroupExceedsBudget_EvictsWithoutChangingBytes(long budget)
    {
        var disc = LargeDisc();
        var content = new MemoryContent(disc.File);
        var cache = new MediaBlockCache(budget);
        var image = await RvzImage.OpenAsync(content, new RvzReadOptions(), cache);
        int readsAfterOpen = content.Reads;
        try
        {
            foreach (int region in new[] { 0, 1, 0 })
            {
                await AssertRegionReadAsync(image, disc, region);
                cache.UsedBytes.ShouldBeLessThanOrEqualTo(budget);
            }
            (content.Reads - readsAfterOpen).ShouldBe(3);
        }
        finally { await image.DisposeAsync(); }

        cache.UsedBytes.ShouldBe(0);
        cache.Snapshot.LiveRentedBytes.ShouldBe(0);
    }

    [Fact]
    public async Task ReadAt_HashExceptionRecordsExceedRemainingBudget_EvictsSourceGroup()
    {
        var disc = LargeDisc();
        // Exact rents remove ArrayPool bucket slack. Payload + one region fit, but the
        // source group's hash exceptions must also consume the same cache budget.
        var cache = new MediaBlockCache(256 * ReferenceDisc.SectorData + RegionSize, new ExactBufferPool());
        var content = new MemoryContent(disc.File);
        var image = await RvzImage.OpenAsync(content, new RvzReadOptions(), cache);
        int readsAfterOpen = content.Reads;
        try
        {
            await AssertRegionReadAsync(image, disc, 0);
            await AssertRegionReadAsync(image, disc, 1);

            (content.Reads - readsAfterOpen).ShouldBe(2);
            cache.UsedBytes.ShouldBeLessThanOrEqualTo(cache.BudgetBytes);
        }
        finally { await image.DisposeAsync(); }

        cache.Snapshot.LiveRentedBytes.ShouldBe(0);
    }

    [Fact]
    public async Task DisposeAsync_PrivateLargeGroupImage_ReleasesSourceAndRegionBuffers()
    {
        var disc = LargeDisc();
        var cache = new MediaBlockCache(64L << 20);
        var image = await Discs.OpenAsync(disc.File, cache);
        await AssertRegionReadAsync(image, disc, 0);
        cache.UsedBytes.ShouldBeGreaterThan(RegionSize);

        await image.DisposeAsync();

        cache.UsedBytes.ShouldBe(0);
        cache.Snapshot.LiveRentedBytes.ShouldBe(0);
    }

    private static async Task AssertRegionReadAsync(RvzImage image, SyntheticRvz disc, int region)
    {
        int offset = PartitionStart + region * RegionSize + 3 * ReferenceDisc.SectorSize;
        byte[] actual = new byte[4096]; // Includes an overridden hash followed by encrypted payload.
        (await image.ReadAtAsync(offset, actual)).ShouldBe(actual.Length);
        actual.ShouldBe(disc.ExpectedDisc.AsSpan(offset, actual.Length).ToArray());
    }

    private static SyntheticRvz LargeDisc(int sectors = 256, RvzMethod method = RvzMethod.Zstd, bool zeroPayload = false)
    {
        byte[] plain = new byte[sectors * ReferenceDisc.SectorData];
        if (!zeroPayload)
            new Random(23).NextBytes(plain);
        var overrides = Enumerable.Range(0, zeroPayload ? 0 : (sectors + 63) / 64)
            .Select(region => new HashOverride(region * 64 + 3, 0x26C, Enumerable.Repeat((byte)(region + 1), 20).ToArray()))
            .ToArray();
        PaddingRun[] padding = zeroPayload ? [] : [new(90L * ReferenceDisc.SectorData + 64, 500, new byte[68])];
        return new SyntheticRvzBuilder(PartitionStart + (long)sectors * ReferenceDisc.SectorSize + 0x8000,
                ChunkSize, method, wii: true)
            .Partition(10, sectors, new byte[16], plain, overrides: overrides, padding: padding)
            .Build();
    }

    private sealed class ExactBufferPool : ArrayPool<byte>
    {
        public override byte[] Rent(int minimumLength) => new byte[minimumLength];
        public override void Return(byte[] array, bool clearArray = false) { }
    }
}
