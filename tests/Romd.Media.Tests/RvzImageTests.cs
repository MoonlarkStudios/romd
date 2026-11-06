using Romd.Media.Rvz;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests;

public sealed class RvzImageTests
{
    [Theory]
    [InlineData(RvzMethod.Zstd)]
    [InlineData(RvzMethod.None)]
    [InlineData(RvzMethod.Bzip2)]
    [InlineData(RvzMethod.Lzma)]
    [InlineData(RvzMethod.Lzma2)]
    public async Task ReadAt_WiiDiscWithEachCompressionMethod_ReconstructsReferenceBytes(RvzMethod method)
    {
        var disc = Discs.Wii(method: method);

        await using var image = await Discs.OpenAsync(disc.File);

        image.Descriptor.HasWiiPartitions.ShouldBeTrue();
        image.Descriptor.Compression.ShouldBe((uint)method);
        (await Discs.ReadAllAsync(image)).ShouldBe(disc.ExpectedDisc);
    }

    [Theory]
    [InlineData(0x8000)]
    [InlineData(0x20000)]
    [InlineData(0x200000)]
    [InlineData(0x400000)]
    public async Task ReadAt_WiiDiscWithEachChunkSize_AppliesHashExceptionsAndPartialRegions(int chunkSize)
    {
        var disc = Discs.Wii(chunkSize);

        await using var image = await Discs.OpenAsync(disc.File);

        (await Discs.ReadAllAsync(image, bufferSize: 777_777)).ShouldBe(disc.ExpectedDisc);
    }

    [Fact]
    public async Task ReadAt_UncompressedGroupsAmongCompressedOnes_AlignDataAfterExceptionLists()
    {
        var disc = Discs.Wii(uncompressed: index => index % 3 == 0);

        await using var image = await Discs.OpenAsync(disc.File);

        (await Discs.ReadAllAsync(image)).ShouldBe(disc.ExpectedDisc);
    }

    [Theory]
    [InlineData(RvzMethod.Zstd)]
    [InlineData(RvzMethod.Lzma)]
    public async Task ReadAt_GameCubeDiscWithPaddingAndZeroChunks_ReconstructsReferenceBytes(RvzMethod method)
    {
        var disc = Discs.GameCube(method: method);

        await using var image = await Discs.OpenAsync(disc.File);

        image.Descriptor.HasWiiPartitions.ShouldBeFalse();
        (await Discs.ReadAllAsync(image)).ShouldBe(disc.ExpectedDisc);
    }

    [Fact]
    public async Task ReadAt_RandomRangesAcrossAreasAndRegions_MatchReferenceBytes()
    {
        var disc = Discs.Wii();
        await using var image = await Discs.OpenAsync(disc.File, new MediaBlockCache(1 << 20));
        var random = new Random(17);
        byte[] actual = new byte[3 << 20];

        for (int i = 0; i < 300; i++)
        {
            long offset = random.NextInt64(0, disc.ExpectedDisc.Length);
            int size = (int)Math.Min(random.Next(1, actual.Length), disc.ExpectedDisc.Length - offset);
            int read = await image.ReadAtAsync(offset, actual.AsMemory(0, size));
            read.ShouldBe(size);
            actual.AsSpan(0, size).SequenceEqual(disc.ExpectedDisc.AsSpan((int)offset, size)).ShouldBeTrue($"offset {offset}, size {size}");
        }
    }

    [Fact]
    public async Task ReadAt_AtAndBeyondEnd_ReturnsZeroAndRejectsNegativeOffsets()
    {
        var disc = Discs.GameCube();
        await using var image = await Discs.OpenAsync(disc.File);
        byte[] buffer = new byte[16];

        (await image.ReadAtAsync(image.Length, buffer)).ShouldBe(0);
        (await image.ReadAtAsync(image.Length - 3, buffer)).ShouldBe(3);
        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await image.ReadAtAsync(-1, buffer));
    }

    [Fact]
    public async Task ReadAt_CancelledToken_ThrowsWithoutDecoding()
    {
        var disc = Discs.Wii();
        await using var image = await Discs.OpenAsync(disc.File);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () => await image.ReadAtAsync(0x100000, new byte[4096], cancelled.Token));
    }

    [Fact]
    public async Task OpenAsync_InvalidFile_DisposesTheArtifact()
    {
        var content = new MemoryContent(new byte[4096]);

        await Should.ThrowAsync<InvalidDataException>(() => RvzImage.OpenAsync(content, new RvzReadOptions(), new MediaBlockCache(0)));

        content.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task DisposeAsync_DisposesTheArtifactAndRejectsFurtherReads()
    {
        var content = new MemoryContent(Discs.GameCube().File);
        var image = await RvzImage.OpenAsync(content, new RvzReadOptions(), new MediaBlockCache(1 << 20));

        await image.DisposeAsync();

        content.Disposed.ShouldBeTrue();
        await Should.ThrowAsync<ObjectDisposedException>(async () => await image.ReadAtAsync(0, new byte[1]));
    }

    [Fact]
    public async Task DisposeAsync_CancelledReaderOfSharedDecode_JoinsDecodeBeforeDisposingArtifact()
    {
        var disc = Discs.Wii();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool armed = false;
        Func<CancellationToken, Task> beforeRead = ct => armed ? gate.Task.WaitAsync(ct) : Task.CompletedTask;
        var ownerContent = new MemoryContent(disc.File) { BeforeRead = beforeRead };
        var cache = new MediaBlockCache(32L << 20);
        var owner = await RvzImage.OpenAsync(ownerContent, new RvzReadOptions(), cache, "shared");
        await using var joiner = await RvzImage.OpenAsync(new MemoryContent(disc.File), new RvzReadOptions(), cache, "shared");
        armed = true;
        using var cancellation = new CancellationTokenSource();
        Task<int> cancelled = owner.ReadAtAsync(0x100000, new byte[4096], cancellation.Token).AsTask();
        byte[] joined = new byte[4096];
        Task<int> healthy = joiner.ReadAtAsync(0x100000, joined).AsTask();
        Task disposal;
        try
        {
            await cancellation.CancelAsync();
            (await Should.ThrowAsync<OperationCanceledException>(() => cancelled)).CancellationToken.ShouldBe(cancellation.Token);
            disposal = owner.DisposeAsync().AsTask();
            disposal.IsCompleted.ShouldBeFalse();
            ownerContent.Disposed.ShouldBeFalse();
            healthy.IsCompleted.ShouldBeFalse();
        }
        finally { gate.SetResult(); }

        (await healthy).ShouldBe(joined.Length);
        await disposal;

        joined.ShouldBe(disc.ExpectedDisc.AsSpan(0x100000, joined.Length).ToArray());
        ownerContent.Disposed.ShouldBeTrue();
    }
}
