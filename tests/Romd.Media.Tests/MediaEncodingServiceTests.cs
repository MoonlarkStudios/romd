using System.Security.Cryptography;
using Romd.Media.Inspection;
using Romd.Media.Nintendo;
using Romd.Media.Rvz;
using Romd.Media.TestSupport;
using Romd.Storage;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests;

public sealed class MediaEncodingServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("romd-child-encode-").FullName;
    private string Input => Path.Combine(_root, "source");
    private string Output => Path.Combine(_root, "output.rvz");
    private async Task<MediaEncodingOutcome> EncodeAsync(byte[] source, long? logicalBytes = null, long? outputCap = null, CancellationToken ct = default, NintendoDiscKind discKind = NintendoDiscKind.GameCube)
    {
        await File.WriteAllBytesAsync(Input, source, ct);
        long logical = logicalBytes ?? source.Length;
        long maximum = ((RvzCanonicalOutputBound.Success)(discKind == NintendoDiscKind.GameCube
            ? RvzCanonicalBounds.GetGameCubeOutputBound(logical, 8L << 20) : RvzCanonicalBounds.GetWiiOutputBound(logical, 8L << 20))).MaximumOutputBytes;
        return await MediaEncodingService.EncodeAsync(Input, Output, 8L << 20, 8L << 20, logical,
            outputCap ?? maximum, discKind, "unix-nice", 10, ct);
    }

    [Fact]
    public async Task EncodeAsync_UnknownKind_RefusesBeforeOpeningSourceOrOutput()
    {
        var result = await MediaEncodingService.EncodeAsync(Input, Output, 8L << 20, 8L << 20, 131072,
            1L << 20, (NintendoDiscKind)99, "unix-nice", 10, CancellationToken.None);
        result.Status.ShouldBe(MediaWorkerStatus.Unsupported);
        result.Encoding.ShouldBeNull();
        File.Exists(Input).ShouldBeFalse();
        File.Exists(Output).ShouldBeFalse();
    }

    [Fact]
    public async Task EncodeGameCubeAsync_ValidIso_ReturnsMeasuredRawArtifactAndRoundTripsLogicalBytes()
    {
        byte[] original = SyntheticNintendoIso.GameCube();
        var result = await EncodeAsync(original);
        result.Status.ShouldBe(MediaWorkerStatus.Encoded);
        var facts = result.Encoding.ShouldNotBeNull();
        byte[] artifact = await File.ReadAllBytesAsync(Output);
        facts.SourceSha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(original)));
        facts.SourceBytes.ShouldBe(original.Length);
        facts.LogicalBytes.ShouldBe(original.Length);
        facts.ArtifactBytes.ShouldBe(artifact.Length);
        facts.ArtifactSha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(artifact)));
        facts.ArtifactMd5.ShouldBe(Convert.ToHexStringLower(MD5.HashData(artifact)));
        facts.ArtifactSha1.ShouldBe(Convert.ToHexStringLower(SHA1.HashData(artifact)));
        facts.RecipeVersion.ShouldBe(RvzCanonicalRecipe.Id);
        facts.EncoderBuild.ShouldBe(RvzEncoder.EncoderBuild);
        await using var image = await RvzImage.OpenAsync(new MemoryContent(artifact), new(), new MediaBlockCache(8L << 20), null);
        byte[] restored = new byte[original.Length];
        int offset = 0;
        while (offset < restored.Length)
            offset += await image.ReadAtAsync(offset, restored.AsMemory(offset));
        restored.ShouldBe(original);
        MediaWorkerProtocol.TryRead(MediaWorkerProtocol.Write(new(MediaWorkerStatus.Encoded,
            new(1, 1, 512L << 20, "decoder", "worker", null, 512L << 20, 0), Encoding: facts)), out var reply).ShouldBeTrue();
        reply!.Inspection.ShouldBeNull();
    }

    [Fact]
    public async Task EncodeGameCubeAsync_RvzInput_ReencodesWithoutDecodedCopy()
    {
        byte[] original = SyntheticNintendoIso.GameCube();
        (await EncodeAsync(original)).Status.ShouldBe(MediaWorkerStatus.Encoded);
        byte[] first = await File.ReadAllBytesAsync(Output);
        File.Delete(Output);
        var result = await EncodeAsync(first, original.Length);
        result.Status.ShouldBe(MediaWorkerStatus.Encoded);
        result.Encoding!.SourceSha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(first)));
        result.Encoding.SourceBytes.ShouldBe(first.Length);
        (await File.ReadAllBytesAsync(Output)).ShouldBe(first);
        Directory.GetFiles(_root).Select(Path.GetFileName).Order(StringComparer.Ordinal).ShouldBe(["output.rvz", "source"]);
    }

    [Theory]
    [InlineData("unknown", MediaWorkerStatus.Unsupported)]
    [InlineData("wii", MediaWorkerStatus.Unsupported)]
    [InlineData("malformed", MediaWorkerStatus.Malformed)]
    [InlineData("wrong-length", MediaWorkerStatus.Malformed)]
    [InlineData("output-limit", MediaWorkerStatus.LimitExceeded)]
    public async Task EncodeGameCubeAsync_UnsupportedInvalidOrUnreservedInput_CreatesNoOutput(string scenario, MediaWorkerStatus expected)
    {
        byte[] original = scenario == "wii" ? SyntheticNintendoIso.Wii(0) : SyntheticNintendoIso.GameCube();
        if (scenario == "unknown")
            original.AsSpan(0x1C, 4).Clear();
        if (scenario == "malformed")
            original.AsSpan(0x420, 4).Fill(0xff);
        var result = await EncodeAsync(original, scenario == "wrong-length" ? original.Length + 1 : null,
            scenario == "output-limit" ? 0x124 : null);
        result.Status.ShouldBe(expected);
        result.Encoding.ShouldBeNull();
        File.Exists(Output).ShouldBeFalse();
    }

    [Fact]
    public async Task EncodeGameCubeAsync_ExistingOutput_RefusesOverwrite()
    {
        await File.WriteAllBytesAsync(Output, [7, 8, 9]);
        var result = await EncodeAsync(SyntheticNintendoIso.GameCube());
        result.Status.ShouldBe(MediaWorkerStatus.IoFailure);
        (await File.ReadAllBytesAsync(Output)).ShouldBe(new byte[] { 7, 8, 9 });
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)1)]
    public async Task EncodeAsync_WiiIsoThenRvz_UsesRequestedBuildDiscTypeAndRoundTripsEveryByte(byte index)
    {
        byte[] original = SyntheticWiiIsoBuilder.Create(WiiTicketVectors.Get(index), sectors: 5, poisonHashes: true).Iso;
        var first = await EncodeAsync(original, discKind: NintendoDiscKind.Wii);
        first.Status.ShouldBe(MediaWorkerStatus.Encoded);
        first.Encoding!.EncoderBuild.ShouldBe(RvzWiiEncoder.EncoderBuild);
        byte[] raw = await File.ReadAllBytesAsync(Output);
        await using (var decoded = await RvzImage.OpenAsync(new MemoryContent(raw), new(), new MediaBlockCache(8L << 20), null))
        {
            decoded.Descriptor.DiscType.ShouldBe(2u);
            decoded.Descriptor.HasWiiPartitions.ShouldBeTrue();
            byte[] restored = new byte[original.Length];
            (await decoded.ReadAtAsync(0, restored)).ShouldBe(restored.Length);
            restored.AsSpan().SequenceEqual(original).ShouldBeTrue("Wii logical bytes, including all poisoned hash blocks, must round trip");
        }
        File.Delete(Output);
        var second = await EncodeAsync(raw, original.Length, discKind: NintendoDiscKind.Wii);
        second.Status.ShouldBe(MediaWorkerStatus.Encoded);
        second.Encoding!.EncoderBuild.ShouldBe(RvzWiiEncoder.EncoderBuild);
        second.Encoding.SourceSha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(raw)));
        second.Encoding.SourceBytes.ShouldBe(raw.Length);
        (await File.ReadAllBytesAsync(Output)).AsSpan().SequenceEqual(raw).ShouldBeTrue("canonical Wii re-encoding is deterministic");
        Directory.GetFiles(_root).Select(Path.GetFileName).Order(StringComparer.Ordinal).ShouldBe(["output.rvz", "source"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EncodeAsync_RequestedKindMismatchesSupportedLayout_RejectsBeforeCreatingOutput(bool requestingWii)
    {
        byte[] original = requestingWii ? SyntheticNintendoIso.GameCube() :
            SyntheticWiiIsoBuilder.Create(WiiTicketVectors.Get(0), sectors: 1).Iso;
        var result = await EncodeAsync(original, discKind: requestingWii ? NintendoDiscKind.Wii : NintendoDiscKind.GameCube);
        result.Status.ShouldBe(MediaWorkerStatus.Unsupported);
        result.Encoding.ShouldBeNull();
        File.Exists(Output).ShouldBeFalse();
    }

    [Fact]
    public async Task EncodeAsync_WiiBoundMinusOne_RejectsBeforeCreatingOutput()
    {
        byte[] original = SyntheticWiiIsoBuilder.Create(WiiTicketVectors.Get(1), sectors: 5, poisonHashes: true).Iso;
        var bound = RvzCanonicalBounds.GetWiiOutputBound(original.Length, 8L << 20).ShouldBeOfType<RvzCanonicalOutputBound.Success>();
        var result = await EncodeAsync(original, outputCap: bound.MaximumOutputBytes - 1, discKind: NintendoDiscKind.Wii);
        result.Status.ShouldBe(MediaWorkerStatus.LimitExceeded);
        result.Encoding.ShouldBeNull();
        File.Exists(Output).ShouldBeFalse();
    }

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
    public void Dispose() => Directory.Delete(_root, true);
}
