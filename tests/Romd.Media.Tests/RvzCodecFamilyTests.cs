using Romd.Media.Nintendo;
using Romd.Media.Rvz;
using Romd.Media.TestSupport;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests;

public sealed class RvzCodecFamilyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EncodeAsync_EachFamily_PreservesCanonicalBytesAndIndependentDecode(bool wii)
    {
        byte[] original = wii ? SyntheticWiiIsoBuilder.Create(WiiTicketVectors.Get(1), sectors: 5, poisonHashes: true).Iso
            : SyntheticNintendoIso.GameCube();
        var kind = wii ? NintendoDiscKind.Wii : NintendoDiscKind.GameCube;
        var family = RvzCodecFamily.For(kind).ShouldNotBeNull();
        var bound = family.GetOutputBound(original.Length, 8L << 20).ShouldBeOfType<RvzCanonicalOutputBound.Success>();
        await using var source = new MemoryContent(original);
        using var expected = new MemoryStream();
        using var actual = new MemoryStream();
        var direct = wii
            ? await new RvzWiiEncoder().EncodeAsync(source, expected, RvzCanonicalBounds.WiiOptions(8L << 20, bound.MaximumOutputBytes))
            : await new RvzEncoder().EncodeGameCubeAsync(source, expected, RvzCanonicalBounds.GameCubeOptions(8L << 20, bound.MaximumOutputBytes));

        var result = await family.EncodeAsync(source, actual, 8L << 20, bound.MaximumOutputBytes);

        var encoded = result.ShouldBeOfType<RvzEncodeResult.Success>();
        encoded.EncoderBuild.ShouldBe(direct.ShouldBeOfType<RvzEncodeResult.Success>().EncoderBuild);
        actual.ToArray().ShouldBe(expected.ToArray());
        await using var decoded = await RvzImage.OpenAsync(new MemoryContent(actual.ToArray()), new(), new MediaBlockCache(0), null);
        decoded.Descriptor.DiscType.ShouldBe(wii ? 2u : 1u);
        decoded.Descriptor.HasWiiPartitions.ShouldBe(wii);
        byte[] restored = new byte[original.Length];
        (await decoded.ReadAtAsync(0, restored)).ShouldBe(restored.Length);
        restored.ShouldBe(original);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void For_EachFamily_RecognizesOnlyItsOwnLayoutAndHeader(bool wii)
    {
        var family = RvzCodecFamily.For(wii ? NintendoDiscKind.Wii : NintendoDiscKind.GameCube).ShouldNotBeNull();
        byte[] own = wii ? SyntheticNintendoIso.Wii(1) : SyntheticNintendoIso.GameCube();
        byte[] other = wii ? SyntheticNintendoIso.GameCube() : SyntheticNintendoIso.Wii(1);
        family.MatchesHeader(own.AsSpan(0, 32)).ShouldBeTrue();
        family.MatchesHeader(other.AsSpan(0, 32)).ShouldBeFalse();
        family.MatchesHeader(own.AsSpan(0, 31)).ShouldBeFalse();
        family.MatchesLayout(wii ? 1 : 0).ShouldBeTrue();
        family.MatchesLayout(wii ? 0 : 1).ShouldBeFalse();
        family.WorkerArgument.ShouldBe(wii ? "wii" : "gamecube");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    public void For_UnknownKind_ReturnsNoFamily(int kind) => RvzCodecFamily.For((NintendoDiscKind)kind).ShouldBeNull();

    [Theory]
    [InlineData("gamecube", NintendoDiscKind.GameCube)]
    [InlineData("wii", NintendoDiscKind.Wii)]
    public void ForWorkerArgument_ExactToken_ResolvesItsEncodingFamily(string token, NintendoDiscKind kind) =>
        RvzCodecFamily.ForWorkerArgument(token).ShouldBeSameAs(RvzCodecFamily.For(kind));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("GameCube")]
    [InlineData("Wii")]
    [InlineData("WII")]
    [InlineData("wii ")]
    [InlineData("psp")]
    public void ForWorkerArgument_UnknownOrNonOrdinalToken_ReturnsNoFamily(string? token) =>
        RvzCodecFamily.ForWorkerArgument(token).ShouldBeNull();

    private sealed class MemoryContent(byte[] bytes) : IRandomAccessContent
    {
        public long Length => bytes.Length;
        public ContentReadIntegrity Integrity => ContentReadIntegrity.None;
        public ValueTask<int> ReadAtAsync(long offset, Memory<byte> destination, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            int count = (int)Math.Min(destination.Length, bytes.Length - offset);
            bytes.AsMemory((int)offset, count).CopyTo(destination);
            return ValueTask.FromResult(count);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
