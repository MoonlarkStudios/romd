using Microsoft.Extensions.DependencyInjection;
using Romd.Media.Rvz;
using Romd.Media.TestSupport;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests;

public sealed class RvzContentStoreTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "romd-media-cas-" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ReadAt_ArtifactStoredAsSeekableZstandard_ReconstructsReferenceBytes()
    {
        // Uncompressed RVZ groups over compressible content make CAS choose seekable zstd, so RVZ
        // group reads cross the store's 64 KiB frame boundaries.
        byte[] raw = Enumerable.Range(0, 0x480000 - 0x80).Select(i => (byte)(i / 97 % 7 + 1)).ToArray();
        byte[] plain = Enumerable.Range(0, Discs.WiiPartitionSectors * ReferenceDisc.SectorData).Select(i => (byte)(i % 251 < 200 ? 0x41 : i)).ToArray();
        var disc = new SyntheticRvzBuilder(0x480000, 0x20000, RvzMethod.None, wii: true, seed: 11)
            .Fill(0x80, raw)
            .Partition(Discs.WiiPartitionFirstSector, Discs.WiiPartitionSectors, new byte[16], plain, firstSegmentSectors: 64)
            .Build();
        await using var services = new ServiceCollection().AddContentAddressableStorage(options =>
        {
            options.RootPath = _root;
            options.SeekableFrameSize = 64 * 1024;
        }).BuildServiceProvider();
        var store = services.GetRequiredService<IContentAddressableStore>();
        var stored = await store.StoreAsync(new MemoryStream(disc.File));
        stored.IsCompressed.ShouldBeTrue();
        var content = (await store.OpenRandomAccessAsync(stored.Key)).ShouldNotBeNull();
        content.Integrity.ShouldBe(ContentReadIntegrity.FrameChecksum);

        await using var image = await RvzImage.OpenAsync(content, new RvzReadOptions(), new MediaBlockCache(1 << 20));

        (await Discs.ReadAllAsync(image, bufferSize: 100_000)).ShouldBe(disc.ExpectedDisc);
    }
}
