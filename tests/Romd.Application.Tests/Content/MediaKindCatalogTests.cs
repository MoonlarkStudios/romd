using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Content;

public sealed class MediaKindCatalogTests
{
    [Fact]
    public void Constructor_EncodedKindsAndOneFallback_ExposesEach()
    {
        var catalog = TestMediaKinds.Production;

        catalog.Fallback.Kind.ShouldBe("opaque");
        catalog.Kinds.Select(kind => kind.Kind).ShouldBe(["gamecube-disc", "wii-disc", "ps2-dvd", "ps1-cd-set"]);
        catalog.Find("wii-disc").ShouldNotBeNull().PlatformKeys.ShouldBe(["wii"]);
        catalog.Find("missing").ShouldBeNull();
    }

    [Fact]
    public void Constructor_CdTrackKindSharingAPlatformWithAWholeFileKind_IsAccepted()
    {
        var catalog = new MediaKindCatalog([.. TestMediaKinds.Descriptors,
            TestMediaKinds.CdTracks("ps2-cd", "ps2", "chd-cd-test/v1",TestMediaKinds.CdFacts)]);

        catalog.Kinds.Where(kind => kind.PlatformKeys.Contains("ps2")).Select(kind => kind.ManifestType)
            .ShouldBe([MediaManifestType.WholeFile, MediaManifestType.CdTracks]);
    }

    [Theory]
    [InlineData("missing-matcher")]
    [InlineData("fallback-matcher")]
    [InlineData("no-fallback")]
    [InlineData("two-fallbacks")]
    [InlineData("fallback-recipe")]
    [InlineData("fallback-platform")]
    [InlineData("fallback-runtime")]
    [InlineData("fallback-original-extension")]
    [InlineData("duplicate-kind")]
    [InlineData("invalid-kind")]
    [InlineData("encoded-without-recipe")]
    [InlineData("invalid-recipe")]
    [InlineData("encoded-without-platform")]
    [InlineData("whole-file-two-platforms")]
    [InlineData("whole-file-without-original-extension")]
    [InlineData("invalid-original-extension")]
    [InlineData("platform-claimed-twice")]
    [InlineData("recipe-across-encodings")]
    [InlineData("profile-across-encodings")]
    [InlineData("original-extensions-disagree")]
    [InlineData("blank-runtime")]
    [InlineData("undefined-manifest-type")]
    [InlineData("cd-tracks-original-extension")]
    [InlineData("duplicate-runtime")]
    [InlineData("duplicate-platform-in-kind")]
    [InlineData("cd-tracks-claimed-twice")]
    [InlineData("profile-with-separator")]
    [InlineData("encoded-without-profiles")]
    [InlineData("recipe-across-layouts")]
    [InlineData("null-kind")]
    [InlineData("undefined-conversion-mode")]
    [InlineData("fallback-automatic")]
    public void Constructor_AmbiguousOrIncompleteRegistration_Throws(string defect)
    {
        var gc = TestMediaKinds.Descriptors.Single(kind => kind.Kind == "gamecube-disc");
        var fallback = TestMediaKinds.Descriptors.Single(kind => kind.Kind == "opaque");
        var others = TestMediaKinds.Descriptors.Where(kind => kind != gc && kind != fallback).ToArray();
        var cd = TestMediaKinds.CdTracks("cd-set", "saturn", "chd-cd-test/v1", TestMediaKinds.CdFacts);
        MediaKindDescriptor?[] descriptors = defect switch
        {
            "missing-matcher" => [gc with { MatchesFacts = null }, fallback, .. others],
            "fallback-matcher" => [gc, fallback with { MatchesFacts = _ => true }, .. others],
            "no-fallback" => [gc, .. others],
            "two-fallbacks" => [gc, fallback, fallback with { Kind = "other-opaque" }, .. others],
            "fallback-recipe" => [gc, fallback with { CurrentRecipeId = "identity-test/v1" }, .. others],
            "fallback-platform" => [gc, fallback with { PlatformKeys = ["snes"] }, .. others],
            "fallback-runtime" => [gc, fallback with { NativeRuntimes = ["any"] }, .. others],
            "fallback-original-extension" => [gc, fallback with { OriginalExtension = ".bin" }, .. others],
            "duplicate-kind" => [gc, gc with { PlatformKeys = ["other"] }, fallback, .. others],
            "invalid-kind" => [gc with { Kind = "GameCube Disc" }, fallback, .. others],
            "encoded-without-recipe" => [gc with { CurrentRecipeId = null }, fallback, .. others],
            "invalid-recipe" => [gc with { CurrentRecipeId = " " }, fallback, .. others],
            "encoded-without-platform" => [gc with { PlatformKeys = [] }, fallback, .. others],
            "whole-file-two-platforms" => [gc with { PlatformKeys = ["gc", "gc-alt"] }, fallback, .. others],
            "whole-file-without-original-extension" => [gc with { OriginalExtension = null }, fallback, .. others],
            "invalid-original-extension" => [gc with { OriginalExtension = "iso" }, fallback, .. others],
            "platform-claimed-twice" => [gc, gc with { Kind = "gamecube-mini-disc" }, fallback, .. others],
            "recipe-across-encodings" => [gc with { CurrentRecipeId = "chd-dvd-canonical/v2" }, fallback, .. others],
            "profile-across-encodings" => [gc with { ProfileVersions = ["chd-observed/v1"] }, fallback, .. others],
            "original-extensions-disagree" => [gc with { OriginalExtension = ".gcm" }, fallback, .. others],
            "blank-runtime" => [gc with { NativeRuntimes = [" "] }, fallback, .. others],
            "undefined-manifest-type" => [gc with { ManifestType = (MediaManifestType)9 }, fallback, .. others],
            "cd-tracks-original-extension" => [gc, cd with { OriginalExtension = ".bin" }, fallback, .. others],
            "duplicate-runtime" => [gc with { NativeRuntimes = ["dolphin", "dolphin"] }, fallback, .. others],
            "duplicate-platform-in-kind" => [cd with { PlatformKeys = ["saturn", "saturn"] }, gc, fallback, .. others],
            "cd-tracks-claimed-twice" => [cd, cd with { Kind = "other-cd-set", CurrentRecipeId = "chd-cd-test/v2" }, gc, fallback, .. others],
            "profile-with-separator" => [gc with { ProfileVersions = ["rvz-import/v1;codec=5"] }, fallback, .. others],
            "encoded-without-profiles" => [gc with { ProfileVersions = [] }, fallback, .. others],
            "recipe-across-layouts" => [gc, cd with { CurrentRecipeId = "chd-dvd-canonical/v2" }, fallback, .. others],
            "undefined-conversion-mode" => [gc with { ConversionMode = (ConversionMode)9 }, fallback, .. others],
            "fallback-automatic" => [gc, fallback with { ConversionMode = ConversionMode.Automatic }, .. others],
            _ => [gc, null, fallback, .. others]
        };

        Should.Throw<ArgumentException>(() => new MediaKindCatalog(descriptors!));
    }

    [Fact]
    public void Constructor_AutomaticCdTrackKind_PreservesItsRegisteredRecipeAndPhysicalMatcher()
    {
        var ps1 = TestMediaKinds.Descriptors.Single(kind => kind.ManifestType == MediaManifestType.CdTracks);
        var catalog = new MediaKindCatalog([.. TestMediaKinds.Descriptors.Where(kind => kind != ps1),
            ps1 with { ConversionMode = ConversionMode.Automatic }]);

        var registered = catalog.Find(ps1.Kind).ShouldNotBeNull();
        registered.ConversionMode.ShouldBe(ConversionMode.Automatic);
        registered.CurrentRecipeId.ShouldBe(ps1.CurrentRecipeId);
        registered.ManifestType.ShouldBe(MediaManifestType.CdTracks);
        registered.MatchesFacts.ShouldNotBeNull();
        catalog.Find("opaque").ShouldNotBeNull().ConversionMode.ShouldBe(ConversionMode.OnRequest);
    }

    [Fact]
    public void Constructor_PlatformClaimedTwice_NamesThePlatformAndLayout()
    {
        var gc = TestMediaKinds.Descriptors[0];

        Should.Throw<ArgumentException>(() => new MediaKindCatalog([.. TestMediaKinds.Descriptors, gc with { Kind = "gamecube-mini-disc" }]))
            .Message.ShouldStartWith("Platform 'gc' is claimed by two WholeFile kinds.");
    }
}
