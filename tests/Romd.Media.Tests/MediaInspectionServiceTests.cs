using System.Security.Cryptography;
using Romd.Media.Inspection;
using Romd.Media.Nintendo;
using Romd.Media.Rvz;
using Romd.Media.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Media.Tests;

public sealed class MediaInspectionServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InspectRvzAsync_CompleteDisc_HashesIndependentArtifactAndDecodedBytes(bool wii)
    {
        var disc = wii ? Discs.Wii() : Discs.GameCube();
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, disc.File);
            var result = await MediaInspectionService.InspectRvzAsync(path, 16L << 20, 16L << 20, CancellationToken.None);
            result.Status.ShouldBe(MediaWorkerStatus.Inspected);
            var facts = result.Inspection.ShouldNotBeNull();
            facts.ArtifactSha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(disc.File)));
            facts.ArtifactBytes.ShouldBe(disc.File.Length);
            facts.LogicalSha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(disc.ExpectedDisc)));
            facts.Sha1.ShouldBe(Convert.ToHexStringLower(SHA1.HashData(disc.ExpectedDisc)));
            facts.Md5.ShouldBe(Convert.ToHexStringLower(MD5.HashData(disc.ExpectedDisc)));
            facts.Crc32.ShouldBe(System.IO.Hashing.Crc32.HashToUInt32(disc.ExpectedDisc).ToString("x8"));
            facts.LogicalBytes.ShouldBe(disc.ExpectedDisc.Length);
            facts.LogicalPrefix.ShouldBe(Convert.ToHexStringLower(disc.ExpectedDisc.AsSpan(0, 32)));
            facts.NintendoLayout.ShouldBeNull();
            RvzInspectionProfile.IsValid(facts.EncodingProfile).ShouldBeTrue();
            var parameters = facts.RvzParameters.ShouldNotBeNull();
            parameters.DiscType.ShouldBe(wii ? 2u : 1u);
            parameters.ChunkSize.ShouldBe(131072);
            parameters.Compression.ShouldBe(5u);
            parameters.HasWiiPartitions.ShouldBe(wii);
            RvzInspectionProfile.Capture(parameters).ShouldBe(facts.EncodingProfile);
            facts.DiscKind.ShouldBeNull("a container's disc-type field does not establish supported Nintendo layout");
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false, (byte)0)]
    [InlineData(true, (byte)0)]
    [InlineData(true, (byte)1)]
    public async Task InspectRvzAsync_EncodedSupportedLayout_ReportsActualDecodedKindAndAllLogicalFacts(bool wii, byte index)
    {
        byte[] original = wii ? SyntheticWiiIsoBuilder.Create(WiiTicketVectors.Get(index), sectors: 5, poisonHashes: true).Iso
            : SyntheticNintendoIso.GameCube();
        NintendoDiscKind kind = wii ? NintendoDiscKind.Wii : NintendoDiscKind.GameCube;
        string directory = Directory.CreateTempSubdirectory("romd-inspected-kind-").FullName;
        try
        {
            string source = Path.Combine(directory, "source.bin"), output = Path.Combine(directory, "encoded.bin");
            await File.WriteAllBytesAsync(source, original);
            var bound = (RvzCanonicalOutputBound.Success)(wii ? RvzCanonicalBounds.GetWiiOutputBound(original.Length, 16L << 20)
                : RvzCanonicalBounds.GetGameCubeOutputBound(original.Length, 16L << 20));
            var encoded = await MediaEncodingService.EncodeAsync(source, output, 16L << 20, 16L << 20, original.Length,
                bound.MaximumOutputBytes, kind, "unix-nice", 10, CancellationToken.None);
            encoded.Status.ShouldBe(MediaWorkerStatus.Encoded);

            var result = await MediaInspectionService.InspectRvzAsync(output, 16L << 20, 16L << 20, CancellationToken.None);

            result.Status.ShouldBe(MediaWorkerStatus.Inspected);
            var facts = result.Inspection.ShouldNotBeNull();
            facts.DiscKind.ShouldBe(kind);
            facts.LogicalPrefix.ShouldBe(Convert.ToHexStringLower(original.AsSpan(0, 32)));
            facts.NintendoLayout.ShouldBe(new MediaNintendoLayout(wii ? 1 : 0, wii && index == 0 ? 1 : 0, wii && index == 1 ? 1 : 0));
            facts.LogicalBytes.ShouldBe(original.Length);
            facts.LogicalSha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(original)));
            facts.Sha1.ShouldBe(Convert.ToHexStringLower(SHA1.HashData(original)));
            facts.Md5.ShouldBe(Convert.ToHexStringLower(MD5.HashData(original)));
            facts.Crc32.ShouldBe(System.IO.Hashing.Crc32.HashToUInt32(original).ToString("x8"));
            facts.EncodingProfile.ShouldBe(MediaWorkerProtocol.CanonicalEncodingProfile);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task InspectRvzAsync_CorruptOrOutsideLimits_ReturnsTypedFailureWithoutFacts()
    {
        var disc = Discs.GameCube();
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, disc.File);
            var large = await MediaInspectionService.InspectRvzAsync(path, disc.File.Length - 1, 16L << 20, CancellationToken.None);
            large.Status.ShouldBe(MediaWorkerStatus.LimitExceeded);
            large.Inspection.ShouldBeNull();
            disc.File[0x30] ^= 1;
            await File.WriteAllBytesAsync(path, disc.File);
            var corrupt = await MediaInspectionService.InspectRvzAsync(path, 16L << 20, 16L << 20, CancellationToken.None);
            corrupt.Status.ShouldBe(MediaWorkerStatus.Malformed);
            corrupt.Inspection.ShouldBeNull();
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InspectIsoAsync_SupportedDisc_HashesEveryOriginalByteAndReportsStructuralFacts(bool wii, bool korean)
    {
        byte[] bytes = wii ? SyntheticNintendoIso.Wii(korean ? (byte)1 : (byte)0) : SyntheticNintendoIso.GameCube();
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, bytes);

            var result = await MediaInspectionService.InspectIsoAsync(path, 16L << 20, 16L << 20, CancellationToken.None);

            result.Status.ShouldBe(MediaWorkerStatus.Inspected);
            var measured = result.Inspection.ShouldNotBeNull();
            measured.ArtifactSha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(bytes)));
            measured.LogicalSha256.ShouldBe(measured.ArtifactSha256);
            measured.ArtifactBytes.ShouldBe(bytes.Length);
            measured.LogicalBytes.ShouldBe(bytes.Length);
            measured.Sha1.ShouldBe(Convert.ToHexStringLower(SHA1.HashData(bytes)));
            measured.Md5.ShouldBe(Convert.ToHexStringLower(MD5.HashData(bytes)));
            measured.Crc32.ShouldBe(System.IO.Hashing.Crc32.HashToUInt32(bytes).ToString("x8"));
            measured.EncodingProfile.ShouldBe("identity/v1");
            measured.LogicalPrefix.ShouldBe(Convert.ToHexStringLower(bytes.AsSpan(0, 32)));
            measured.NintendoLayout.ShouldBe(new MediaNintendoLayout(wii ? 1 : 0, wii && !korean ? 1 : 0, korean ? 1 : 0));
            var iso = result.IsoInspection.ShouldNotBeNull();
            iso.Status.ShouldBe(NintendoIsoInspectionStatus.Eligible);
            iso.Profile.ShouldBe(NintendoIsoInspector.ProfileVersion);
            iso.Layout!.Kind.ShouldBe(wii ? NintendoDiscKind.Wii : NintendoDiscKind.GameCube);
            iso.Layout.KoreanKeyPartitions.ShouldBe(korean ? 1 : 0);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4096)]
    public async Task InspectIsoAsync_UnknownContentIncludingTinyFiles_ReturnsFullyMeasuredUnsupportedOriginal(int bytes)
    {
        byte[] content = Enumerable.Repeat((byte)0x5A, bytes).ToArray();
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, content);

            var result = await MediaInspectionService.InspectIsoAsync(path, 16L << 20, 16L << 20, CancellationToken.None);

            result.Status.ShouldBe(MediaWorkerStatus.Inspected);
            result.Inspection!.ArtifactSha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(content)));
            result.Inspection.LogicalBytes.ShouldBe(bytes);
            result.Inspection.LogicalPrefix.ShouldBe(Convert.ToHexStringLower(content.AsSpan(0, Math.Min(bytes, 32))));
            result.Inspection.NintendoLayout.ShouldBeNull();
            result.Inspection.Sha1.ShouldBe(Convert.ToHexStringLower(SHA1.HashData(content)));
            result.IsoInspection!.Status.ShouldBe(NintendoIsoInspectionStatus.Unsupported);
            result.IsoInspection.Layout.ShouldBeNull();
            MediaWorkerProtocol.TryRead(MediaWorkerProtocol.Write(new(result.Status,
                new(41, 41, 512L << 20, "decoder/exact", "worker/exact", null, 512L << 20, 0),
                result.Inspection, result.IsoInspection)), out _).ShouldBeTrue();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task InspectIsoAsync_KnownMagicWithTruncatedLayout_FailsWithoutMeasurementOrEligibility()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, SyntheticNintendoIso.GameCube()[..32]);

            var result = await MediaInspectionService.InspectIsoAsync(path, 16L << 20, 16L << 20, CancellationToken.None);

            result.Status.ShouldBe(MediaWorkerStatus.Malformed);
            result.Inspection.ShouldBeNull();
            result.IsoInspection.ShouldBeNull();
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InspectIsoAsync_ArtifactOrLogicalLimit_FailsWithoutEligibility(bool logicalLimit)
    {
        byte[] bytes = SyntheticNintendoIso.GameCube();
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, bytes);

            var result = await MediaInspectionService.InspectIsoAsync(path,
                logicalLimit ? bytes.Length : bytes.Length - 1,
                logicalLimit ? bytes.Length - 1 : bytes.Length, CancellationToken.None);

            result.Status.ShouldBe(MediaWorkerStatus.LimitExceeded);
            result.Inspection.ShouldBeNull();
            result.IsoInspection.ShouldBeNull();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task InspectIsoAsync_Cancellation_PropagatesInsteadOfUnsupportedClaim()
    {
        string path = Path.GetTempFileName();
        using var cancellation = new CancellationTokenSource();
        try
        {
            await File.WriteAllBytesAsync(path, SyntheticNintendoIso.GameCube());
            cancellation.Cancel();

            await Should.ThrowAsync<OperationCanceledException>(() =>
                MediaInspectionService.InspectIsoAsync(path, 16L << 20, 16L << 20, cancellation.Token));
        }
        finally { File.Delete(path); }
    }
}
