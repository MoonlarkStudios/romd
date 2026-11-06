using Romd.Admin.Application.Source.Rom;
using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Source.Rom;

public sealed class RomMediaPolicyRegistryTests
{
    private static RomContainerFacts.Rvz Rvz => new(0x01000000, 1, 5, 19, 131072, false, 4096);
    [Fact]
    public void Classify_ConflictingPolicies_RoutesKnownKindAndFailsClosedUnknownKind()
    {
        var registry = new RomMediaPolicyRegistry([
            new Policy(RomIsoDiscKind.GameCube, "gc", "a/v1", RomRepresentationProfileClass.Canonical),
            new Policy(RomIsoDiscKind.Wii, "wii", "b/v1", RomRepresentationProfileClass.NonCanonical)]);
        registry.Classify(Rvz, RomIsoDiscKind.GameCube).ShouldBe(RomRepresentationProfileClass.Canonical);
        registry.Classify(Rvz, RomIsoDiscKind.Wii).ShouldBe(RomRepresentationProfileClass.NonCanonical);
        registry.Classify(Rvz, null).ShouldBe(RomRepresentationProfileClass.Unknown);
        registry.Classify(RomContainerFacts.Identity.Instance, RomIsoDiscKind.GameCube).ShouldBe(RomRepresentationProfileClass.Unknown);
        registry.Classify(new RomContainerFacts.Chd(5, 4096, 4096, 2048, 1, [0, 0, 0, 0], false, Sha1.Parse(new string('1', 40)), Sha1.Parse(new string('2', 40)), []), null).ShouldBe(RomRepresentationProfileClass.Unknown);
        registry.Classify(Rvz, (RomIsoDiscKind)99).ShouldBe(RomRepresentationProfileClass.Unknown);
    }
    [Fact]
    public void ResolveRecipe_CompatibleEncodingAndObservedKind_RequireOneUnambiguousRecipe()
    {
        var registry = new RomMediaPolicyRegistry([
            new Policy(RomIsoDiscKind.GameCube, "gc", "a/v1"), new Policy(RomIsoDiscKind.Wii, "wii", "b/v1")]);
        registry.ResolveRecipe(RomRepresentationEncoding.Rvz, null).ShouldBeNull();
        registry.ResolveRecipe(RomRepresentationEncoding.Rvz, RomIsoDiscKind.GameCube).ShouldBe("a/v1");
        registry.ResolveRecipe(RomRepresentationEncoding.Rvz, RomIsoDiscKind.Wii).ShouldBe("b/v1");
        registry.ResolveRecipe(RomRepresentationEncoding.Chd, RomIsoDiscKind.GameCube).ShouldBeNull();
        registry.ResolveRecipe(RomRepresentationEncoding.Identity, null).ShouldBeNull();
        new RomMediaPolicyRegistry([new Policy(RomIsoDiscKind.GameCube, "gc", "a/v1"),
            new Policy(RomIsoDiscKind.Wii, "wii", "a/v1")])
            .ResolveRecipe(RomRepresentationEncoding.Rvz, null).ShouldBe("a/v1");
    }
    [Fact]
    public void Constructor_InvalidDeclarations_RejectsBeforeUse()
    {
        Should.Throw<ArgumentException>(() => new RomMediaPolicyRegistry([]));
        Should.Throw<ArgumentException>(() => new RomMediaPolicyRegistry([new Policy(RomIsoDiscKind.GameCube, "gc", "a/v1"),
            new Policy(RomIsoDiscKind.GameCube, "gc", "b/v1")]));
        Should.Throw<ArgumentException>(() => new RomMediaPolicyRegistry([new Policy(RomIsoDiscKind.GameCube, "gc", "bad\n")]));
        Should.Throw<ArgumentException>(() => new RomMediaPolicyRegistry([new Policy(RomIsoDiscKind.GameCube, "", "a/v1")]));
        Should.Throw<ArgumentException>(() => new RomMediaPolicyRegistry([new Policy(RomIsoDiscKind.GameCube, "gc", "a/v1",
            encoding: RomRepresentationEncoding.Identity)]));
        Should.Throw<ArgumentException>(() => new RomMediaPolicyRegistry([new Policy(RomIsoDiscKind.GameCube, "gc", "a/v1",
            encoding: (RomRepresentationEncoding)99)]));
    }
    [Fact]
    public void Constructor_MissingEncodingProfile_RejectsBeforeUse()
    {
        var policy = new MutablePolicy { EncodingProfile = null! };
        Should.Throw<ArgumentException>(() => new RomMediaPolicyRegistry([policy]));
    }
    [Fact]
    public void Constructor_RegistrationArrayMutation_DoesNotChangePolicySet()
    {
        IRomMediaTypePolicy[] policies = [new Policy(RomIsoDiscKind.GameCube, "gc", "a/v1")];
        var registry = new RomMediaPolicyRegistry(policies);
        policies[0] = new Policy(RomIsoDiscKind.Wii, "wii", "b/v1");
        registry.ForDisc(RomIsoDiscKind.GameCube).ShouldNotBeNull();
        registry.ForDisc(RomIsoDiscKind.Wii).ShouldBeNull();
        registry.ResolveRecipe(RomRepresentationEncoding.Rvz, RomIsoDiscKind.GameCube).ShouldBe("a/v1");
    }
    [Fact]
    public void Constructor_StrategyMetadataMutation_DoesNotInvalidateValidatedRegistration()
    {
        var strategy = new MutablePolicy();
        var registry = new RomMediaPolicyRegistry([strategy]);
        strategy.DiscKind = RomIsoDiscKind.Wii;
        strategy.PlatformKey = "wii";
        strategy.RecipeVersion = "changed/v1";
        strategy.StoredEncoding = RomRepresentationEncoding.Identity;
        strategy.EncodingProfile = new Policy(RomIsoDiscKind.Wii, "wii", "replacement/v1", RomRepresentationProfileClass.NonCanonical);
        var registered = registry.ForDisc(RomIsoDiscKind.GameCube).ShouldNotBeNull();
        registered.DiscKind.ShouldBe(RomIsoDiscKind.GameCube);
        registered.PlatformKey.ShouldBe("gc");
        registered.EncodingProfile.RecipeVersion.ShouldBe("a/v1");
        registered.EncodingProfile.StoredEncoding.ShouldBe(RomRepresentationEncoding.Rvz);
        registry.ForDisc(RomIsoDiscKind.Wii).ShouldBeNull();
        registry.ResolveRecipe(RomRepresentationEncoding.Rvz, RomIsoDiscKind.GameCube).ShouldBe("a/v1");
        registry.Classify(Rvz, RomIsoDiscKind.GameCube).ShouldBe(RomRepresentationProfileClass.Canonical);
    }
    private sealed class MutablePolicy : IRomMediaTypePolicy, IRomMediaEncodingProfile
    {
        public MutablePolicy() => EncodingProfile = this;
        public IRomMediaEncodingProfile EncodingProfile { get; set; }
        public bool ObservesEncodingLayout(RomLogicalInspection inspection) => false;
        public RomIsoDiscKind DiscKind { get; set; } = RomIsoDiscKind.GameCube;
        public string PlatformKey { get; set; } = "gc";
        public RomRepresentationEncoding StoredEncoding { get; set; } = RomRepresentationEncoding.Rvz;
        public string RecipeVersion { get; set; } = "a/v1";
        public RomRepresentationProfileClass Classify(RomContainerFacts container) => RomRepresentationProfileClass.Canonical;
        public bool IsEncodingEligible(RomContentDescriptor content, CancellationToken ct) => throw new InvalidOperationException("This fixture does not authorize encoding.");
        public bool MatchesFacts(RomContentDescriptor content) => throw new InvalidOperationException("These registry tests do not infer kind from media content.");
        public long GetMaximumOutputBytes(long logicalBytes, long maxLogicalBytes, CancellationToken ct = default) => logicalBytes;
    }
    private sealed class Policy(RomIsoDiscKind kind, string platform, string recipe,
        RomRepresentationProfileClass classification = RomRepresentationProfileClass.Unknown,
        RomRepresentationEncoding encoding = RomRepresentationEncoding.Rvz) : IRomMediaTypePolicy, IRomMediaEncodingProfile
    {
        public IRomMediaEncodingProfile EncodingProfile => this;
        public bool ObservesEncodingLayout(RomLogicalInspection inspection) => false;
        public RomIsoDiscKind DiscKind => kind;
        public string PlatformKey => platform;
        public RomRepresentationEncoding StoredEncoding => encoding;
        public string RecipeVersion => recipe;
        public RomRepresentationProfileClass Classify(RomContainerFacts container) => classification;
        public bool IsEncodingEligible(RomContentDescriptor content, CancellationToken ct) => throw new InvalidOperationException("This fixture does not authorize encoding.");
        public bool MatchesFacts(RomContentDescriptor content) => throw new InvalidOperationException("These registry tests do not infer kind from media content.");
        public long GetMaximumOutputBytes(long logicalBytes, long maxLogicalBytes, CancellationToken ct = default) => logicalBytes;
    }
}
