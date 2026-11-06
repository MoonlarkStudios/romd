using Microsoft.Extensions.DependencyInjection;
using Romd.Media.Probe;
using Romd.Media.Rvz;
using Romd.Media.TestSupport;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class MeasuredProbeContentTests
{
    [Fact]
    public async Task WebProbe_IndependentCompleteMeasurements_ServeLogicalAndExactContainerBytes()
    {
        string root = Directory.CreateTempSubdirectory("romd-measured-probe-").FullName;
        try
        {
            await using var services = new ServiceCollection().AddContentAddressableStorage(options =>
                { options.RootPath = Path.Combine(root, "cas"); options.WritePolicy = ContentWritePolicy.Raw; }).BuildServiceProvider();
            var cas = services.GetRequiredService<IContentAddressableStore>();
            byte[] logical = GameCubeIsoFixture.Create(4 * 1024 * 1024);
            string iso = Path.Combine(root, "independent.iso");
            await File.WriteAllBytesAsync(iso, logical);
            var expected = await Digests.FileAsync(iso);
            var first = await EncodeAsync(cas, logical, root, "first.rvz", 32768);
            var second = await EncodeAsync(cas, logical, root, "second.rvz", 131072);
            first.Key.ShouldNotBe(second.Key);
            var measured = await MeasuredProbeContent.VerifyRvzAsync(cas, first.Key, expected, default);
            measured.Logical.Identity.Sha256.ToString().ShouldBe(expected.Sha256);
            measured.Opaque.Identity.Sha256.ToString().ShouldBe(first.Hashes.Sha256);
            measured.Logical.Preferred.Container.ShouldBeOfType<Romd.Domain.Source.Rom.RomContainerFacts.Rvz>().BlockBytes.ShouldBe(32768);
            await WebChecks.Run(cas, first, second, expected, iso);
            (await Digests.FileAsync(iso)).ShouldBe(expected);
            (await Digests.FileAsync(first.Path)).ShouldBe(first.Hashes);
            (await Digests.FileAsync(second.Path)).ShouldBe(second.Hashes);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task VerifyRvz_ForeignIndependentOracle_RefusesMeasurement()
    {
        string root = Directory.CreateTempSubdirectory("romd-measured-probe-foreign-").FullName;
        try
        {
            await using var services = new ServiceCollection().AddContentAddressableStorage(options =>
                { options.RootPath = Path.Combine(root, "cas"); options.WritePolicy = ContentWritePolicy.Raw; }).BuildServiceProvider();
            var cas = services.GetRequiredService<IContentAddressableStore>();
            byte[] logical = GameCubeIsoFixture.Create(131072);
            var artifact = await EncodeAsync(cas, logical, root, "disc.rvz", 32768);
            await using var foreign = new MemoryStream(GameCubeIsoFixture.Create(131072, seed: 42));
            var expected = await Digests.StreamAsync(foreign, default);
            var error = await Should.ThrowAsync<InvalidOperationException>(() => MeasuredProbeContent.VerifyRvzAsync(cas, artifact.Key, expected, default));
            error.Message.ShouldContain("complete probe output equals independently measured original hashes");
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<Artifact> EncodeAsync(IContentAddressableStore cas, byte[] logical, string root, string name, int blockBytes)
    {
        string path = Path.Combine(root, name);
        await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite))
        {
            var result = await new RvzEncoder().EncodeGameCubeAsync(new Bytes(logical), output, new(RvzEncodeCompression.Zstandard, 3, blockBytes));
            result.ShouldBeOfType<RvzEncodeResult.Success>();
        }
        var hashes = await Digests.FileAsync(path);
        await using var input = File.OpenRead(path);
        var stored = await cas.StoreAsync(input);
        return new(path, stored.Key, hashes);
    }
    private sealed class Bytes(byte[] bytes) : IRandomAccessContent
    {
        public long Length => bytes.Length;
        public ContentReadIntegrity Integrity => ContentReadIntegrity.None;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            int take = (int)Math.Min(destination.Length, Math.Max(0, Length - offset));
            if (take > 0)
                bytes.AsMemory((int)offset, take).CopyTo(destination);
            return ValueTask.FromResult(take);
        }
    }
}
