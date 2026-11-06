using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Source.Rom;

public sealed class RomVerificationCapturedProfileTests
{
    [Theory]
    [InlineData(RomRepresentationEncoding.Identity)]
    [InlineData(RomRepresentationEncoding.Rvz)]
    public void Create_HistoricalObservation_PreservesUnknownCapturedProfile(RomRepresentationEncoding encoding)
    {
        var proof = Create(encoding);
        proof.CapturedEncodingProfile.ShouldBeNull();
        proof.Profile.ShouldBe("inspection/v1");
    }

    [Fact]
    public void Create_ActualProfileAtMaximumLength_PreservesObservation()
    {
        string captured = new('p', 512);
        Create(RomRepresentationEncoding.Rvz, captured).CapturedEncodingProfile.ShouldBe(captured);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Create_EmptyCapturedProfile_RejectsMissingObservation(string invalid)
    {
        Should.Throw<ArgumentException>(() => Create(RomRepresentationEncoding.Rvz, invalid));
    }

    [Fact]
    public void Create_CapturedProfileAboveMaximumLength_RejectsOversizedObservation()
    {
        Should.Throw<ArgumentException>(() => Create(RomRepresentationEncoding.Rvz, new string('p', 513)));
    }

    [Fact]
    public void Create_IdentityWithContainerProfile_RejectsContradictoryEvidence()
    {
        Should.Throw<ArgumentException>(() => Create(RomRepresentationEncoding.Identity, "rvz-canonical/v1"));
        Create(RomRepresentationEncoding.Identity, RomEncodingProfiles.Identity)
            .CapturedEncodingProfile.ShouldBe(RomEncodingProfiles.Identity);
    }

    private static RomVerification Create(RomRepresentationEncoding encoding, string? captured = null)
    {
        var hash = Sha256.Parse(new string('a', 64));
        return new(hash, 256, encoding, hash, 256, Sha1.Parse(new string('b', 40)), Md5.Parse(new string('c', 32)),
            Crc32.Parse("12345678"), "inspection/v1", "decoder", "verifier", DateTimeOffset.UtcNow,
            capturedEncodingProfile: captured);
    }
}
