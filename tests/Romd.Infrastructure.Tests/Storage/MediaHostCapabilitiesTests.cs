using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Media.PlayStation;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

/// <remarks>Synthetic resident identities stand in for builds verified at startup; they do not qualify a native build.</remarks>
public sealed class MediaHostCapabilitiesTests
{
    [Theory]
    [InlineData("linux-x64", "linux-x64", true)]
    [InlineData("linux-arm64", "linux-arm64", false)]
    [InlineData("linux-x64", "linux-arm64", false)]
    [InlineData("linux-arm64", "linux-x64", false)]
    public void CanEncode_PspQualification_DoesNotRestrictSharedDvdDecoding(string decoderRid, string encoderRid, bool encodes)
    {
        var capabilities = new MediaHostCapabilities(() => Native(decoderRid), () => Tool(encoderRid), Production, encodes: true);
        capabilities.CanDecode("psp-umd").ShouldBeTrue();
        capabilities.CanDecode(Ps2Dvd).ShouldBeTrue();
        capabilities.CanEncode("psp-umd").ShouldBe(encodes);
        capabilities.QualifiedRuntime("psp-umd").ShouldBeNull();
    }

    private const string Ps1Cd = "ps1-cd-set";
    private const string Ps2Dvd = "ps2-dvd";
    private static readonly RomMediaComposition Production = RomMediaPolicyComposition.Create();

    [Theory]
    [InlineData("linux-x64", true)]
    [InlineData("linux-arm64", false)]
    public void CanDecode_ProductionRegistration_QualifiesTheCdKindOnlyOnItsRuntime(string rid, bool qualified)
    {
        var capabilities = Host(Native(rid));

        capabilities.CanDecode(Ps1Cd).ShouldBe(qualified);
        capabilities.CanDecode(Ps2Dvd).ShouldBeTrue();
        capabilities.Decoder(RomRepresentationEncoding.Chd).ShouldBe(new RomChdDecoderBuild(rid, Sha1.Parse(new string('a', 40)), 2));
    }

    [Fact]
    public void CanDecode_NoVerifiedDecoder_ReadsOnlyMediaThatNeedsNoNativeBuild()
    {
        var capabilities = Host(null);

        capabilities.Decoder(RomRepresentationEncoding.Chd).ShouldBeNull();
        capabilities.CanDecode(Ps1Cd).ShouldBeFalse();
        capabilities.CanDecode(Ps2Dvd).ShouldBeFalse();
        capabilities.CanDecode("gamecube-disc").ShouldBeTrue();
        capabilities.CanDecode("wii-disc").ShouldBeTrue();
        capabilities.CanDecode(Production.Kinds.Fallback.Kind).ShouldBeTrue();
        capabilities.CanDecode("unregistered").ShouldBeFalse();
    }

    [Fact]
    public void Decoder_OnlyChdReadsThroughANativeBuild()
    {
        var capabilities = Host(Native("linux-x64"));

        capabilities.Decoder(RomRepresentationEncoding.Rvz).ShouldBeNull();
        capabilities.Decoder(RomRepresentationEncoding.Identity).ShouldBeNull();
    }

    [Fact]
    public void Answers_InvalidatedDeployment_StopAnsweringAtOnce()
    {
        ChdNativeIdentity? resident = Native("linux-x64");
        var capabilities = new MediaHostCapabilities(() => resident, () => Tool("linux-x64"), Production, encodes: true);
        capabilities.CanDecode(Ps1Cd).ShouldBeTrue();
        capabilities.CanEncode(Ps1Cd).ShouldBeTrue();

        resident = null;

        capabilities.Decoder(RomRepresentationEncoding.Chd).ShouldBeNull();
        capabilities.CanDecode(Ps1Cd).ShouldBeFalse();
        capabilities.CanEncode(Ps1Cd).ShouldBeFalse();
    }

    [Theory]
    [InlineData("linux-x64", "linux-x64", true)]
    [InlineData("linux-arm64", "linux-arm64", false)]
    [InlineData("linux-x64", "linux-arm64", false)]
    public void CanEncode_Worker_RequiresQualifiedDecoderAndEncoderBuilds(string decoderRid, string encoderRid, bool encodes)
    {
        var capabilities = new MediaHostCapabilities(() => Native(decoderRid), () => Tool(encoderRid), Production, encodes: true);

        capabilities.CanEncode(Ps1Cd).ShouldBe(encodes);
        capabilities.CanEncode(Ps2Dvd).ShouldBeTrue();
        capabilities.CanEncode("gamecube-disc").ShouldBeTrue();
        capabilities.CanEncode(Production.Kinds.Fallback.Kind).ShouldBeFalse();
    }

    [Fact]
    public void CanEncode_WorkerWithoutVerifiedEncoder_EncodesOnlyManagedFormats()
    {
        var capabilities = new MediaHostCapabilities(() => Native("linux-x64"), () => null, Production, encodes: true);

        capabilities.CanEncode(Ps1Cd).ShouldBeFalse();
        capabilities.CanEncode(Ps2Dvd).ShouldBeFalse();
        capabilities.CanEncode("wii-disc").ShouldBeTrue();
        capabilities.CanDecode(Ps1Cd).ShouldBeTrue();
    }

    [Fact]
    public void CanEncode_DecodeOnlyHost_EncodesNothingAndNeverAsksForAnEncoder()
    {
        var capabilities = new MediaHostCapabilities(() => Native("linux-x64"),
            () => throw new InvalidOperationException("A decode-only host asked for an encoder build."), Production, encodes: false);

        Production.Kinds.Kinds.Append(Production.Kinds.Fallback).ShouldAllBe(kind => !capabilities.CanEncode(kind.Kind));
        capabilities.CanDecode(Ps1Cd).ShouldBeTrue();
    }

    [Fact]
    public void Qualifies_RecordedBuilds_FollowTheRegistrationsRuntime()
    {
        var capabilities = Host(null);

        capabilities.QualifiedRuntime(Ps1Cd).ShouldBe("linux-x64");
        capabilities.QualifiedRuntime(Ps2Dvd).ShouldBeNull();
        capabilities.Qualifies(Ps1Cd, "linux-x64").ShouldBeTrue();
        capabilities.Qualifies(Ps1Cd, "linux-arm64").ShouldBeFalse();
        capabilities.Qualifies(Ps1Cd, "").ShouldBeFalse();
        capabilities.Qualifies(Ps2Dvd, "linux-arm64").ShouldBeTrue();
        capabilities.Qualifies("unregistered", "linux-x64").ShouldBeFalse();
    }

    [Theory]
    [InlineData("linux-x64", false)]
    [InlineData("linux-arm64", true)]
    public void Answers_RegistrationQualifiesAnotherRuntime_FollowIt(string rid, bool qualified)
    {
        var media = RomMediaPolicyComposition.Create(RomMediaPolicyComposition.Registrations.Select(kind =>
            kind.Kind == Ps1Cd ? kind with { QualifiedRuntime = "linux-arm64" } : kind).ToArray());
        var capabilities = new MediaHostCapabilities(() => Native(rid), () => Tool(rid), media, encodes: true);

        capabilities.QualifiedRuntime(Ps1Cd).ShouldBe("linux-arm64");
        capabilities.CanDecode(Ps1Cd).ShouldBe(qualified);
        capabilities.CanEncode(Ps1Cd).ShouldBe(qualified);
        capabilities.Qualifies(Ps1Cd, rid).ShouldBe(qualified);
    }

    private static MediaHostCapabilities Host(ChdNativeIdentity? resident) => new(() => resident, () => null, Production, encodes: false);

    private static ChdNativeIdentity Native(string rid) => new(rid, "libmoonlark_chdr.so", 100, new string('c', 64), new string('a', 40),
        "test-only-upstream", 2, "test-only-family", new string('e', 64), true, true, true);

    private static ChdmanToolIdentity Tool(string rid) => new(rid, "mame0289", new string('d', 64), 1, 1, new string('e', 64));
}
