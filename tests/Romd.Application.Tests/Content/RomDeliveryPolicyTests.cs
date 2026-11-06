using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Content;

public sealed class RomDeliveryPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("snes")]
    [InlineData("GC")]
    [InlineData(" gc")]
    public void For_UnregisteredOrNonCanonicalKey_ReturnsNull(string? platformKey) =>
        TestDeliveryPolicies.Production.For(platformKey).ShouldBeNull();

    [Fact]
    public void For_RegisteredKey_ReturnsItsStoredFormatAndRuntimes()
    {
        var stored = TestDeliveryPolicies.Production.For("ps2").ShouldNotBeNull();

        stored.Format.ShouldBe(RomDeliveryFormat.Chd);
        stored.NativeRuntimes.ShouldBe(["pcsx2"]);
    }

    [Fact]
    public void For_CdTrackKind_KeepsItsPlatformOutOfStoredDelivery()
    {
        var policy = TestDeliveryPolicies.Production;

        policy.For("psx").ShouldBeNull();
        policy.PlatformKeys.Order(StringComparer.Ordinal).ShouldBe(["gc", "ps2", "wii"]);
        policy.OriginalExtension(RomRepresentationEncoding.Chd).ShouldBe(".iso");
    }

    [Fact]
    public void Constructor_CopiesRuntimes_SoLaterCallerMutationCannotChangePolicy()
    {
        var runtimes = new List<string> { "dolphin" };
        var policy = new RomDeliveryPolicy(TestDeliveryPolicies.Formats, new MediaKindCatalog([
            TestMediaKinds.Disc("gamecube-disc", "gc", RomRepresentationEncoding.Rvz, "rvz-canonical/v1", runtimes, ["rvz-import/v1"],TestMediaKinds.Descriptors[0].MatchesFacts!),
            TestMediaKinds.Production.Fallback]));

        runtimes.Add("other");

        policy.For("gc")!.NativeRuntimes.ShouldBe(["dolphin"]);
    }

    [Theory]
    [InlineData(RomRepresentationEncoding.Rvz, RomDeliveryFormat.Rvz)]
    [InlineData(RomRepresentationEncoding.Chd, RomDeliveryFormat.Chd)]
    public void StoredFormat_EncodedEncoding_ReturnsTheFormatThatDeliversIt(RomRepresentationEncoding encoding,
        RomDeliveryFormat expected) =>
        RomDeliveryPolicy.StoredFormat(encoding).ShouldBe(expected);

    [Fact]
    public void StoredFormat_Identity_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => RomDeliveryPolicy.StoredFormat(RomRepresentationEncoding.Identity));

    [Theory]
    [InlineData(RomRepresentationEncoding.Rvz, ".iso")]
    [InlineData(RomRepresentationEncoding.Chd, ".iso")]
    [InlineData(RomRepresentationEncoding.Identity, null)]
    public void OriginalExtension_Encoding_ReturnsItsWholeFileKindsExtension(RomRepresentationEncoding encoding, string? expected) =>
        TestDeliveryPolicies.Production.OriginalExtension(encoding).ShouldBe(expected);

    [Theory]
    [InlineData("rvz-import/v1;codec=5;level=19", "rvz-import/v1")]
    [InlineData("chd-observed/v1;version=5", "chd-observed/v1")]
    [InlineData("identity/v1", "identity/v1")]
    [InlineData("chd-cd-whole-files/v1", "chd-cd-whole-files/v1")]
    [InlineData("chd-cd-whole-files/v2", "unknown")]
    [InlineData("", "unknown")]
    [InlineData(null, "unknown")]
    public void ProfileVersion_Profile_ReportsOnlyRegisteredVersions(string? profile, string expected) =>
        TestDeliveryPolicies.Production.ProfileVersion(profile).ShouldBe(expected);

    [Fact]
    public void Constructor_StoredFormatNamedLikeAnOriginal_Throws()
    {
        var formats = new RomDeliveryFormatCatalog([
            new(RomDeliveryFormat.Original, "Original", null), new(RomDeliveryFormat.Rvz, "RVZ", ".iso"),
            new(RomDeliveryFormat.Chd, "CHD", ".chd")]);

        Should.Throw<ArgumentException>(() => new RomDeliveryPolicy(formats, TestMediaKinds.Production));
    }

    [Fact]
    public void IsCanonicalChd_RegisteredKind_UsesItsMatcherAndCopiesTheRegistration()
    {
        var parameters = new RomContainerFacts.Chd(5, 8192, 16384, 2048, 1, [RomChdCodecs.Zstd, 0, 0, 0], false, DeliveryFacts.Short('a'), DeliveryFacts.Short('c'), [new(0x44564420, 1, [0])]);
        var matchers = new Dictionary<string, Func<RomContainerFacts.Chd, bool>> { ["ps2-dvd"] = facts => facts.HunkBytes == 16384 };
        var policy = new RomDeliveryPolicy(TestDeliveryPolicies.Formats, TestMediaKinds.Production, matchers);
        matchers["ps2-dvd"] = _ => false;

        policy.IsCanonicalChd("ps2-dvd", parameters).ShouldBeTrue();
        policy.IsCanonicalChd("unknown", parameters).ShouldBeFalse();
        TestDeliveryPolicies.Production.IsCanonicalChd("ps2-dvd", parameters).ShouldBeFalse();
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("opaque")]
    [InlineData("ps1-cd-set")]
    [InlineData("gamecube-disc")]
    public void Constructor_CanonicalChdMatcherWithoutAWholeFileChdKind_Throws(string kind) =>
        Should.Throw<ArgumentException>(() => new RomDeliveryPolicy(TestDeliveryPolicies.Formats, TestMediaKinds.Production,
            new Dictionary<string, Func<RomContainerFacts.Chd, bool>> { [kind] = _ => true }));

    [Fact]
    public void Constructor_WithoutFormatsOrKinds_Throws()
    {
        Should.Throw<ArgumentNullException>(() => new RomDeliveryPolicy(null!, TestMediaKinds.Production));
        Should.Throw<ArgumentNullException>(() => new RomDeliveryPolicy(TestDeliveryPolicies.Formats, null!));
    }
}
