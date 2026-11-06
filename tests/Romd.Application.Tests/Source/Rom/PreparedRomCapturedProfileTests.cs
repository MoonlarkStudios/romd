using NSubstitute;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Source.Rom;

public sealed class PreparedRomCapturedProfileTests
{
    [Fact]
    public void Create_CapturedProfileDiffersFromMetadata_RejectsUnboundProfile()
    {
        Should.Throw<InvalidDataException>(() => Create("observed", "declared"));
    }

    [Fact]
    public void Create_CapturedProfileMatchesMetadata_PreservesFreshObservation()
    {
        var prepared = Create("observed", "observed");
        prepared.Verification.CapturedEncodingProfile.ShouldBe("observed");
        prepared.EncodingProfile.ShouldBe("observed");
    }

    [Fact]
    public void Create_NoDisplayObservation_StillBindsCapturedPhysicalParameters()
    {
        var prepared = Create(null, "declared");
        prepared.EncodingProfile.ShouldBe("declared");
        prepared.Verification.CapturedEncodingProfile.ShouldBeNull();
        prepared.Container.ShouldBeOfType<RomContainerFacts.Rvz>().CompressionLevel.ShouldBe(12);
        prepared.Measurement.ObservedParametersSha256.ShouldBe(prepared.Container.Fingerprint);
    }

    private static PreparedRom.WholeFile Create(string? captured, string declared)
    {
        var artifact = Substitute.For<ITempFile>();
        var hash = Sha256.Parse(new string('a', 64));
        var sha1 = Sha1.Parse(new string('b', 40));
        var md5 = Md5.Parse(new string('c', 32));
        var crc32 = Crc32.Parse("12345678");
        artifact.FileSha256.Returns(hash);
        artifact.FileSize.Returns(256);
        var proof = new RomVerification(hash, 256, RomRepresentationEncoding.Rvz, hash, 256, sha1, md5, crc32,
            "inspection/v1", "decoder", "verifier", DateTimeOffset.UtcNow, capturedEncodingProfile: captured);
        return new(artifact, "disc.iso", hash, new(sha1, md5, crc32, 256), RomRepresentationEncoding.Rvz,
            proof, new RomContainerFacts.Rvz(0x01000000, 1, 5, 12, 131072, false, 256), encodingProfile: declared);
    }
}
