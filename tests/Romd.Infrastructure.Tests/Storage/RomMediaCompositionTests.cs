using NSubstitute;
using Romd.Domain.Hashing;
using Romd.Admin.Application.Source.Rom;
using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Storage;

public sealed class RomMediaCompositionTests
{
    private static readonly RomMediaKindRegistration GameCube = RomMediaPolicyComposition.Registrations.Single(kind => kind.Kind == "gamecube-disc");
    private static readonly RomMediaKindRegistration Ps2Dvd = RomMediaPolicyComposition.Registrations.Single(kind => kind.Kind == "ps2-dvd");
    private static readonly RomMediaKindRegistration Ps1Cd = RomMediaPolicyComposition.Registrations.Single(kind => kind.Kind == "ps1-cd-set");

    [Fact]
    public void Create_Production_RegistersEachKindWithItsExistingRecipe()
    {
        var composition = RomMediaPolicyComposition.Create();

        composition.Kinds.Kinds.Select(Describe).ShouldBe([
            "gamecube-disc gc Rvz rvz-canonical/v1 WholeFile Automatic [dolphin] .iso [rvz-import/v1, rvz-canonical/v1]",
            "wii-disc wii Rvz rvz-canonical/v1 WholeFile Automatic [dolphin] .iso [rvz-import/v1, rvz-canonical/v1]",
            "ps2-dvd ps2 Chd chd-dvd-canonical/v2 WholeFile Automatic [pcsx2] .iso [chd-observed/v1]",
            "psp-umd psp Chd chd-dvd-canonical/v2 WholeFile Automatic [] .iso [chd-observed/v1]",
            "ps1-cd-set psx Chd chd-ps1-canonical/v1 CdTracks Automatic []  [chd-cd-whole-files/v1, chd-cd-retained-files/v2, chd-cd-inspection/v2]"]);
        Describe(composition.Kinds.Fallback).ShouldBe("opaque  Identity  WholeFile OnRequest []  [identity/v1]");
        composition.Registry.Policies.Select(policy => (policy.PlatformKey, policy.DiscKind, policy.EncodingProfile.RecipeVersion)).ShouldBe([
            ("gc", RomIsoDiscKind.GameCube, "rvz-canonical/v1"), ("wii", RomIsoDiscKind.Wii, "rvz-canonical/v1"),
            ("ps2", RomIsoDiscKind.PlayStation2Dvd, "chd-dvd-canonical/v2"),
            ("psp", RomIsoDiscKind.PlayStationPortableUmd, "chd-dvd-canonical/v2")]);
        composition.Reconciliation["gamecube-disc"].Kind.ConversionMode.ShouldBe(ConversionMode.Automatic);
        composition.Reconciliation["wii-disc"].Kind.ConversionMode.ShouldBe(ConversionMode.Automatic);
        composition.Reconciliation["ps2-dvd"].Kind.ConversionMode.ShouldBe(ConversionMode.Automatic);
    }

    [Fact]
    public void Create_EncodingProfileReplacement_PreservesTheCapturedRecipeAcrossEveryView()
    {
        var original = Substitute.For<IRomMediaEncodingProfile>();
        original.StoredEncoding.Returns(RomRepresentationEncoding.Chd);
        original.RecipeVersion.Returns(Ps2Dvd.Recipe.Id);
        original.Classify(Arg.Any<RomContainerFacts>()).Returns(RomRepresentationProfileClass.Canonical);
        original.GetMaximumOutputBytes(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(1000);
        var strategy = Substitute.For<IRomMediaTypePolicy>();
        strategy.DiscKind.Returns(RomIsoDiscKind.PlayStation2Dvd);
        strategy.PlatformKey.Returns("ps2");
        strategy.EncodingProfile.Returns(original);
        var composition = RomMediaPolicyComposition.Create([Ps2Dvd with { WholeFilePolicy = _ => strategy }]);
        var replacement = Substitute.For<IRomMediaEncodingProfile>();
        replacement.StoredEncoding.Returns(RomRepresentationEncoding.Rvz);
        replacement.RecipeVersion.Returns("replacement/v1");
        replacement.Classify(Arg.Any<RomContainerFacts>()).Returns(RomRepresentationProfileClass.NonCanonical);
        replacement.GetMaximumOutputBytes(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(2000);
        strategy.EncodingProfile.Returns(replacement);
        var parameters = new RomContainerFacts.Chd(5, 2048, 16384, 2048, 1, [0, 0, 0, 0], false,
            Sha1.Parse(new string('a', 40)), Sha1.Parse(new string('b', 40)), []);

        composition.Registry.ResolveRecipe(RomRepresentationEncoding.Chd, RomIsoDiscKind.PlayStation2Dvd).ShouldBe(Ps2Dvd.Recipe.Id);
        composition.Registry.Classify(parameters, RomIsoDiscKind.PlayStation2Dvd).ShouldBe(RomRepresentationProfileClass.Canonical);
        composition.Reconciliation[Ps2Dvd.Kind].MatchesParameters(parameters).ShouldBeTrue();
        composition.DeliveryPolicy.IsCanonicalChd(Ps2Dvd.Kind, parameters).ShouldBeTrue();
        composition.WholeEncoding[Ps2Dvd.Kind].MaximumOutputBytes(128, 4096, default).ShouldBe(1000);
    }

    [Fact]
    public void Create_WholeFilePolicyFactory_RunsOnceForKindMatchingAndRecipeRegistration()
    {
        int calls = 0;
        var registration = GameCube with
        {
            WholeFilePolicy = platform => { calls++; return new RvzMediaTypePolicy(RvzDiscFamily.GameCube, platform); }
        };
        var composition = RomMediaPolicyComposition.Create([registration]);
        calls.ShouldBe(1);
        composition.Registry.Policies.ShouldHaveSingleItem().EncodingProfile.RecipeVersion.ShouldBe(GameCube.Recipe.Id);
        composition.Kinds.Find(GameCube.Kind).ShouldNotBeNull();
    }

    [Fact]
    public void Create_Production_DerivesTheCdRecipeFromThePs1Registration()
    {
        var recipe = RomMediaPolicyComposition.Create().CdRecipe.ShouldNotBeNull();

        Ps1Cd.Recipe.ShouldBe(new RomMediaRecipe("chd-ps1-canonical/v1", RomRepresentationEncoding.Chd,
            "cd;hunk=39168;codecs=63647a73,6364666c,00000000,00000000", MediaRecipeRebuild.MatchingParameters));
        (recipe.Id, recipe.HunkBytes).ShouldBe(("chd-ps1-canonical/v1", 39168u));
        recipe.Codecs.ShouldBe([0x63647a73u, 0x6364666cu, 0u, 0u]);
        recipe.CodecNames.ShouldBe(["cdzs", "cdfl"]);
        recipe.PlatformKeys.ShouldBe(["psx"]);
    }

    [Fact]
    public void Create_Production_QualifiesPspEncodingWithoutRestrictingSharedDvdReads()
    {
        var composition = RomMediaPolicyComposition.Create();

        composition.QualifiedRuntimes.ShouldBe(new Dictionary<string, string> { ["ps1-cd-set"] = "linux-x64" });
        composition.QualifiedEncodingRuntimes.ShouldBe(new Dictionary<string, string> { ["psp-umd"] = "linux-x64" });
        composition.CdRecipe.ShouldNotBeNull().Kind.ShouldBe("ps1-cd-set");
    }

    [Fact]
    public void Create_CdTrackKindWithoutAQualifiedRuntime_NamesTheMissingRuntime() =>
        Should.Throw<ArgumentException>(() => RomMediaPolicyComposition.Create([Ps2Dvd, Ps1Cd with { QualifiedRuntime = null }]))
            .Message.ShouldContain("rebuilds CD tracks, so it names the runtime it is qualified on");

    [Theory]
    [InlineData("")]
    [InlineData(" linux-x64")]
    [InlineData("Linux-X64")]
    [InlineData("linux/x64")]
    public void Create_QualifiedRuntimeThatIsNoRuntimeIdentifier_Throws(string runtime) =>
        Should.Throw<ArgumentException>(() => RomMediaPolicyComposition.Create([Ps2Dvd with { QualifiedRuntime = runtime }, Ps1Cd]))
            .Message.ShouldContain("names a qualified runtime that is not a runtime identifier");

    [Fact]
    public void Create_KindsWithoutACdTrackKind_HaveNoCdRecipe() =>
        RomMediaPolicyComposition.Create([GameCube, Ps2Dvd]).CdRecipe.ShouldBeNull();

    [Fact]
    public void Create_OnRequestWholeFileKind_ReachesTheRegistryFromItsDescriptor()
    {
        var wii = RomMediaPolicyComposition.Registrations.Single(kind => kind.Kind == "wii-disc");

        var composition = RomMediaPolicyComposition.Create([GameCube with { ConversionMode = ConversionMode.OnRequest }, wii]);

        composition.Kinds.Find("gamecube-disc").ShouldNotBeNull().ConversionMode.ShouldBe(ConversionMode.OnRequest);
        composition.Reconciliation["gamecube-disc"].Kind.ConversionMode.ShouldBe(ConversionMode.OnRequest);
        composition.Reconciliation["wii-disc"].Kind.ConversionMode.ShouldBe(ConversionMode.Automatic);
    }

    [Fact]
    public void Create_ProductionCdKind_IsAutomaticAndConfinedToItsQualifiedRuntime()
    {
        var composition = RomMediaPolicyComposition.Create();
        composition.Kinds.Find("ps1-cd-set").ShouldNotBeNull().ConversionMode.ShouldBe(ConversionMode.Automatic);
        composition.Reconciliation["ps1-cd-set"].Kind.ConversionMode.ShouldBe(ConversionMode.Automatic);
        composition.QualifiedRuntimes["ps1-cd-set"].ShouldBe("linux-x64");
    }

    [Theory]
    [InlineData("cd-tracks-without-recipe")]
    [InlineData("whole-file-with-cd-recipe")]
    [InlineData("recipe-with-another-id")]
    [InlineData("recipe-with-other-parameters")]
    [InlineData("recipe-on-another-platform")]
    [InlineData("recipe-for-another-kind")]
    [InlineData("no-recipe-built")]
    [InlineData("two-cd-track-kinds")]
    public void Create_CdRecipeDisagreesWithItsKind_Throws(string defect)
    {
        RomMediaKindRegistration[] registrations = defect switch
        {
            "cd-tracks-without-recipe" => [Ps1Cd with { CdRecipe = null }],
            "whole-file-with-cd-recipe" => [GameCube with { CdRecipe = Ps1Cd.CdRecipe }],
            "recipe-with-another-id" => [Ps1Cd with { Recipe = Ps1Cd.Recipe with { Id = "chd-ps1-canonical/v2" } }],
            "recipe-with-other-parameters" => [Ps1Cd with
            {
                CdRecipe = (kind, platforms) => new RomCdStorageRecipe(kind, Ps1Cd.Recipe.Id, 2448 * 8, [0x63647a73, 0x6364666c, 0, 0], platforms)
            }],
            "recipe-on-another-platform" => [Ps1Cd with
            {
                CdRecipe = (kind, _) => new RomCdStorageRecipe(kind, Ps1Cd.Recipe.Id, 39168, [0x63647a73, 0x6364666c, 0, 0], ["saturn"])
            }],
            "recipe-for-another-kind" => [Ps1Cd with
            {
                CdRecipe = (_, platforms) => new RomCdStorageRecipe("saturn-cd-set", Ps1Cd.Recipe.Id, 39168, [0x63647a73, 0x6364666c, 0, 0], platforms)
            }],
            "no-recipe-built" => [Ps1Cd with { CdRecipe = (_, _) => null! }],
            _ => [Ps1Cd, Ps1Cd with
            {
                Kind = "saturn-cd-set", PlatformKeys = ["saturn"],
                Recipe = Ps1Cd.Recipe with { Id = "chd-saturn-canonical/v1", Parameters = "cd;hunk=39168;codecs=63647a73,00000000,00000000,00000000" },
                CdRecipe = (kind, platforms) => new RomCdStorageRecipe(kind, "chd-saturn-canonical/v1", 39168, [0x63647a73, 0, 0, 0], platforms)
            }]
        };

        Should.Throw<ArgumentException>(() => RomMediaPolicyComposition.Create(
                defect == "whole-file-with-cd-recipe" ? registrations : [Ps2Dvd, .. registrations]))
            .Message.ShouldContain(defect switch
            {
                "cd-tracks-without-recipe" or "whole-file-with-cd-recipe" => "needs a CD recipe exactly when it rebuilds CD tracks",
                "two-cd-track-kinds" => "Only one media kind may rebuild CD tracks",
                _ => "has a CD recipe for another kind, recipe, platform or parameters"
            });
    }

    [Fact]
    public void Create_KindsSharingOneRecipeDefinition_AreAccepted()
    {
        var wii = RomMediaPolicyComposition.Registrations.Single(kind => kind.Kind == "wii-disc");
        wii.Recipe.ShouldBe(GameCube.Recipe);

        RomMediaPolicyComposition.Create([GameCube, wii]).Registry.Policies.Select(policy => policy.EncodingProfile.RecipeVersion)
            .ShouldBe(["rvz-canonical/v1", "rvz-canonical/v1"]);
    }

    [Theory]
    [InlineData("parameters")]
    [InlineData("encoding")]
    [InlineData("rebuild")]
    public void Create_RecipeIdDefinedTwiceDifferently_Throws(string difference)
    {
        var redefined = difference switch
        {
            "parameters" => GameCube.Recipe with { Parameters = "codec=5;level=22;chunk=131072" },
            "rebuild" => GameCube.Recipe with { Rebuild = MediaRecipeRebuild.Required("Replace earlier output.") },
            _ => GameCube.Recipe with { Encoding = RomRepresentationEncoding.Chd }
        };

        Should.Throw<ArgumentException>(() => RomMediaPolicyComposition.Create(
            [GameCube, GameCube with { Kind = "wii-disc", PlatformKeys = ["wii"], Recipe = redefined }]));
    }

    [Theory]
    [InlineData("whole-file-without-policy")]
    [InlineData("cd-tracks-with-policy")]
    [InlineData("policy-on-another-platform")]
    [InlineData("policy-with-another-recipe")]
    [InlineData("policy-with-another-encoding")]
    [InlineData("no-policy-built")]
    public void Create_WholeFilePolicyPolicyDisagreesWithItsKind_Throws(string defect)
    {
        RomMediaKindRegistration registration = defect switch
        {
            "whole-file-without-policy" => GameCube with { WholeFilePolicy = null },
            "cd-tracks-with-policy" => GameCube with
            {
                ManifestType = MediaManifestType.CdTracks,
                ConversionMode = ConversionMode.OnRequest,
                OriginalExtension = null
            },
            "policy-on-another-platform" => GameCube with { WholeFilePolicy = _ => new RvzMediaTypePolicy(RvzDiscFamily.GameCube, "wii") },
            "policy-with-another-recipe" => GameCube with { Recipe = GameCube.Recipe with { Id = "rvz-canonical/v2" } },
            "policy-with-another-encoding" => Ps2Dvd with
            {
                WholeFilePolicy = platform => new MisencodedPolicy(platform, Ps2Dvd.Recipe.Id, RomRepresentationEncoding.Rvz)
            },
            _ => GameCube with { WholeFilePolicy = _ => null! }
        };

        Should.Throw<ArgumentException>(() => RomMediaPolicyComposition.Create([registration]));
    }

    [Fact]
    public void Create_Ps1CdKind_StaysOutOfWholeFilePolicyAndStoredDelivery()
    {
        var composition = RomMediaPolicyComposition.Create();

        composition.Kinds.Find("ps1-cd-set").ShouldNotBeNull().ManifestType.ShouldBe(MediaManifestType.CdTracks);
        composition.Registry.Policies.ShouldNotContain(policy => policy.PlatformKey == "psx");
        composition.DeliveryPolicy.For("psx").ShouldBeNull();
        composition.DeliveryPolicy.PlatformKeys.Order(StringComparer.Ordinal).ShouldBe(["gc", "ps2", "psp", "wii"]);
        composition.DeliveryPolicy.OriginalExtension(RomRepresentationEncoding.Chd).ShouldBe(".iso");
    }

    private static string Describe(MediaKindDescriptor kind) =>
        $"{kind.Kind} {string.Join(",", kind.PlatformKeys)} {kind.StoredEncoding} {kind.CurrentRecipeId} {kind.ManifestType} " +
        $"{kind.ConversionMode} [{string.Join(", ", kind.NativeRuntimes)}] {kind.OriginalExtension} [{string.Join(", ", kind.ProfileVersions)}]";

    /// <summary>Matches its kind's platform and recipe but stores another encoding.</summary>
    private sealed class MisencodedPolicy(string platformKey, string recipe, RomRepresentationEncoding encoding)
        : IRomMediaTypePolicy, IRomMediaEncodingProfile
    {
        public IRomMediaEncodingProfile EncodingProfile => this;
        public bool ObservesEncodingLayout(RomLogicalInspection inspection) => false;
        public RomIsoDiscKind DiscKind => RomIsoDiscKind.PlayStation2Dvd;
        public string PlatformKey => platformKey;
        public RomRepresentationEncoding StoredEncoding => encoding;
        public string RecipeVersion => recipe;
        public RomRepresentationProfileClass Classify(RomContainerFacts container) => RomRepresentationProfileClass.Unknown;
        public bool MatchesFacts(RomContentDescriptor content) => false;
        public bool IsEncodingEligible(RomContentDescriptor content, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return MatchesFacts(content);
        }
        public long GetMaximumOutputBytes(long logicalBytes, long maxLogicalBytes, CancellationToken ct = default) => 0;
    }
}
