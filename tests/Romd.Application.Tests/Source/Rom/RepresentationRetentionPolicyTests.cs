using Romd.Admin.Application.Hashing;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Application.Tests.Source.Rom;

public sealed class RepresentationRetentionPolicyTests
{
    private static readonly Sha256 Hash = Sha256.Parse(new string('a', 64));
    [Theory]
    [InlineData(RomRepresentationEncoding.Identity, 0, RomRepresentationEncoding.Rvz, 12, RepresentationRetention.Retain)]
    [InlineData(RomRepresentationEncoding.Rvz, 12, RomRepresentationEncoding.Rvz, 19, RepresentationRetention.Retain)]
    [InlineData(RomRepresentationEncoding.Rvz, 19, RomRepresentationEncoding.Rvz, 12, RepresentationRetention.Discard)]
    [InlineData(RomRepresentationEncoding.Rvz, 19, RomRepresentationEncoding.Rvz, 19, RepresentationRetention.Discard)]
    [InlineData(RomRepresentationEncoding.Rvz, 12, RomRepresentationEncoding.Rvz, 12, RepresentationRetention.Discard)]
    [InlineData(RomRepresentationEncoding.Rvz, 12, RomRepresentationEncoding.Identity, 0, RepresentationRetention.Discard)]
    public void Decide_DifferentArtifact_RanksMeasuredParametersAndReleasesOnlyLowerOwners(
        RomRepresentationEncoding retainedEncoding, int retainedLevel, RomRepresentationEncoding incomingEncoding, int incomingLevel, RepresentationRetention expected)
    {
        var prior = Retained(7, 'b', Container(retainedEncoding, retainedLevel), true, true);
        var decision = RepresentationRetentionPolicy.Decide([prior], Prepared(incomingEncoding, incomingLevel), TestRomMediaPolicies.Create());
        decision.Retention.ShouldBe(expected);
        decision.PreferredArtifact.ShouldBe(expected == RepresentationRetention.Retain ? Hash : prior.ArtifactSha256);
        decision.Release.ShouldBe(expected == RepresentationRetention.Retain ? [prior] : []);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Decide_ExactCanonicalArtifact_RetainsNewMeasurementAndReleasesOnlyWeakerOutputs(bool alreadyMeasured)
    {
        var container = Container(RomRepresentationEncoding.Rvz, 19);
        var fingerprint = container.Fingerprint;
        var exact = new RetainedRepresentation(17, 107, 7, Hash, container, true, alreadyMeasured);
        var alternate = Retained(8, 'b', Container(RomRepresentationEncoding.Rvz, 12), false, true);
        var decision = RepresentationRetentionPolicy.Decide([exact, alternate], Prepared(RomRepresentationEncoding.Rvz, 19), TestRomMediaPolicies.Create());
        decision.Retention.ShouldBe(RepresentationRetention.EvidenceOnly);
        decision.PreferredArtifact.ShouldBe(Hash);
        decision.Release.ShouldBe([alternate]);
        exact.Container.Fingerprint.ShouldBe(fingerprint);
    }

    [Fact]
    public void Decide_StrongerAlternate_PreventsDowngradeWithoutPruning()
    {
        RetainedRepresentation[] existing = [Retained(1, 'b', RomContainerFacts.Identity.Instance, true, true), Retained(2, 'c', Container(RomRepresentationEncoding.Rvz, 19), false, true)];
        var decision = RepresentationRetentionPolicy.Decide(existing, Prepared(RomRepresentationEncoding.Rvz, 12), TestRomMediaPolicies.Create());
        decision.Retention.ShouldBe(RepresentationRetention.Discard);
        decision.PreferredArtifact.ShouldBe(existing[0].ArtifactSha256);
        decision.Release.ShouldBeEmpty();
    }

    [Fact]
    public void Decide_CanonicalObservation_ReleasesAllLowerRankedOwnedOutputs()
    {
        RetainedRepresentation[] existing = [Retained(1,'b',RomContainerFacts.Identity.Instance,true,true),
            Retained(2,'c',Container(RomRepresentationEncoding.Rvz,12),false,true),Retained(3,'d',Container(RomRepresentationEncoding.Rvz,7),false,true)];
        var decision = RepresentationRetentionPolicy.Decide(existing, Prepared(RomRepresentationEncoding.Rvz, 19), TestRomMediaPolicies.Create());
        decision.Retention.ShouldBe(RepresentationRetention.Retain);
        decision.PreferredArtifact.ShouldBe(Hash);
        decision.Release.ShouldBe(existing);
    }

    [Fact]
    public void Decide_UnmeasuredCanonicalHeader_DoesNotOutrankMeasuredBytes()
    {
        var prior = Retained(7, 'b', Container(RomRepresentationEncoding.Rvz, 19), true, false);
        var decision = RepresentationRetentionPolicy.Decide([prior], Prepared(RomRepresentationEncoding.Rvz, 12), TestRomMediaPolicies.Create());
        decision.Retention.ShouldBe(RepresentationRetention.Retain);
        decision.Release.ShouldBe([prior]);
    }

    [Fact]
    public void Decide_UnknownMeasuredParameters_OutrankOriginalWithoutCanonicalClaim()
    {
        var prior = Retained(7, 'b', RomContainerFacts.Identity.Instance, true, true);
        var prepared = Prepared(RomRepresentationEncoding.Rvz, 7);
        TestRomMediaPolicies.Create().Classify(prepared.Container, null).ShouldBe(RomRepresentationProfileClass.Unknown);
        var decision = RepresentationRetentionPolicy.Decide([prior], prepared, TestRomMediaPolicies.Create());
        decision.Retention.ShouldBe(RepresentationRetention.Retain);
        decision.Release.ShouldBe([prior]);
    }

    [Theory]
    [InlineData(12, "canonical")]
    [InlineData(19, "unknown/imported")]
    public void Decide_DisplayProfileLabel_DoesNotOverrideMeasuredParameters(int level, string display)
    {
        var original = Retained(7, 'b', Container(RomRepresentationEncoding.Rvz, 19), true, true);
        var decision = RepresentationRetentionPolicy.Decide([original], Prepared(RomRepresentationEncoding.Rvz, level, display), TestRomMediaPolicies.Create());
        decision.Retention.ShouldBe(RepresentationRetention.Discard);
        decision.Release.ShouldBeEmpty();
    }

    [Fact]
    public void CreateNew_RequiresLogicalHashAndNonnegativeSize()
    {
        var prepared = Prepared(RomRepresentationEncoding.Identity, 0);
        Should.Throw<ArgumentException>(() => RomFile.CreateNew("disc.iso", 1, 100, default, prepared.Hashes.Sha1, prepared.Hashes.Md5, prepared.Hashes.Crc32, now: DateTimeOffset.UnixEpoch));
        Should.Throw<ArgumentOutOfRangeException>(() => RomFile.CreateNew("disc.iso", 1, -1, Hash, prepared.Hashes.Sha1, prepared.Hashes.Md5, prepared.Hashes.Crc32, now: DateTimeOffset.UnixEpoch));
        RomFile.CreateNew("disc.iso", 1, 100, Hash, prepared.Hashes.Sha1, prepared.Hashes.Md5, prepared.Hashes.Crc32, now: DateTimeOffset.UnixEpoch).ContentSha256.ShouldBe(Hash);
    }
    private static RetainedRepresentation Retained(int file, char hash, RomContainerFacts container, bool preferred, bool measured) =>
        new(file + 10, file + 100, file, Sha256.Parse(new string(hash, 64)), container, preferred, measured);
    internal static RomContainerFacts Container(RomRepresentationEncoding encoding, int level) => encoding == RomRepresentationEncoding.Identity
        ? RomContainerFacts.Identity.Instance : new RomContainerFacts.Rvz(0x01000000, 1, 5, level, 131072, false, 100);
    private static PreparedRom.WholeFile Prepared(RomRepresentationEncoding encoding, int level, string? display = null)
    {
        var hashes = new DatHashResult(Sha1.Parse(new string('b', 40)), Md5.Parse(new string('c', 32)), Crc32.FromUInt32(1), 100);
        return new(new Artifact(), "disc.iso", Hash, hashes, encoding, new(Hash, 100, encoding, Hash, 100, hashes.Sha1, hashes.Md5, hashes.Crc32,
            "complete-output/v1", "fixture-managed-reader", "fixture-verifier", DateTimeOffset.UnixEpoch), Container(encoding, level), encodingProfile: display);
    }
    private sealed class Artifact : ITempFile
    {
        public bool TryBeginChildUse() => false; public bool TryEndChildUse() => false; public bool Quarantine() => false;
        public string Path => "unused-retention-fixture"; public Sha256 FileSha256 => Hash; public long FileSize => 100;
        public Stream OpenRead() => throw new InvalidOperationException("Pure retention does not read bytes.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
