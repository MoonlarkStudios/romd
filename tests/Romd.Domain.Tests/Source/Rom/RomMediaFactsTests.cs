using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Shouldly;
using Xunit;

namespace Romd.Domain.Tests.Source.Rom;

public sealed class RomMediaFactsTests
{
    private static Sha256 Hash(char value) => Sha256.Parse(new string(value, 64));
    private static Sha1 ShortHash(char value) => Sha1.Parse(new string(value, 40));
    private static RomLogicalIdentity Identity(long size, char value) =>
        new(size, Hash(value), ShortHash(value), Md5.Parse(new string(value, 32)), Crc32.FromUInt32(0));
    private static RomDecoderIdentity.Native Native(string rid) => new(new(rid, ShortHash('f'), 2));

    [Fact]
    public void LogicalIdentity_ZeroCrc_IsMeasuredAndValid() => Identity(0, 'a').Crc32.IsEmpty.ShouldBeFalse();

    [Fact]
    public void DecoderIdentity_SameNativeTuple_HasValueEquality()
    {
        Native("linux-x64").ShouldBe(Native("linux-x64"));
        Native("linux-x64").ShouldNotBe(Native("linux-arm64"));
        new RomDecoderIdentity.Managed(Native("linux-x64").Key).ShouldNotBe((RomDecoderIdentity)Native("linux-x64"));
    }

    [Fact]
    public void CdFacts_TwoDecoderBuilds_RetainsBothAndAllIndependentOutputs()
    {
        var facts = Cd();
        facts.Verifications.Count.ShouldBe(2);
        facts.Verifications.ShouldAllBe(proof => proof.Outputs.Count == 2);
        facts.Verifications[0].ComputedRawSha1.ShouldNotBe(facts.Outputs[0].Identity.Sha1);
        facts.Descriptors.Single().FileId.ShouldBe(30);
    }

    [Theory]
    [InlineData("partial-proof")]
    [InlineData("descriptor-proof")]
    [InlineData("artifact")]
    [InlineData("integrity")]
    [InlineData("duplicate-build")]
    [InlineData("missing-descriptor")]
    [InlineData("wrong-descriptor")]
    [InlineData("missing-proof")]
    [InlineData("descriptor-as-output")]
    public void CdFacts_IncompleteOrContradictoryGraph_Rejects(string mutation) =>
        Should.Throw<ArgumentException>(() => Cd(mutation));

    [Fact]
    public void WholeFile_IdentityArtifactMismatch_Rejects()
    {
        var output = new RomRepresentationOutput(1, 1, RomOutputSelector.Whole.Instance, Identity(100, 'a'));
        Should.Throw<ArgumentException>(() => new RomRepresentationFacts(1, 1, Hash('b'), 100, RomContainerFacts.Identity.Instance,
            RomReconstructionManifest.WholeFile.Instance, [output], [], []));
    }

    [Fact]
    public void WholeFile_MultipleWholeOutputs_RejectsWithArgumentError()
    {
        var output = new RomRepresentationOutput(1, 1, RomOutputSelector.Whole.Instance, Identity(100, 'a'));
        var other = new RomRepresentationOutput(2, 1, RomOutputSelector.Whole.Instance, Identity(100, 'a'));
        Should.Throw<ArgumentException>(() => new RomRepresentationFacts(1, 1, Hash('a'), 100, RomContainerFacts.Identity.Instance,
            RomReconstructionManifest.WholeFile.Instance, [output, other], [], []));
    }

    [Fact]
    public void WholeFile_ManagedBuildMeasurements_AreDistinctAndCaptured()
    {
        var logical = Identity(100, 'a');
        var selector = RomOutputSelector.Whole.Instance;
        var container = new RomContainerFacts.Rvz(0x30000, 1, 5, 22, 131072, false, 100);
        RomRepresentationOutput[] outputs = [new(1, 1, selector, logical)];
        RomRepresentationVerification[] proofs = [Proof("managed-a"), Proof("managed-b")];
        RomRepresentationVerification Proof(string build) => new(Guid.NewGuid(), Hash('b'), 512, new RomDecoderIdentity.Managed(build),
            "checked-output/test", "verifier", DateTimeOffset.UnixEpoch, [new(selector, logical)], container);
        var facts = new RomRepresentationFacts(1, 1, Hash('b'), 512, container,
            RomReconstructionManifest.WholeFile.Instance, outputs, proofs, []);
        outputs[0] = new(2, 2, selector, Identity(101, 'c'));
        proofs[0] = Proof("managed-c");
        facts.Outputs[0].Id.ShouldBe(1);
        facts.Verifications[0].Decoder.ShouldBe(new RomDecoderIdentity.Managed("managed-a"));
        facts.Verifications.Count.ShouldBe(2);
    }

    [Fact]
    public void WholeFile_VerifiedParameterSnapshotDisagrees_Rejects()
    {
        var logical = Identity(100, 'a');
        var selector = RomOutputSelector.Whole.Instance;
        var observed = new RomContainerFacts.Rvz(0x30000, 1, 3, 7, 32768, false, 100);
        var declared = new RomContainerFacts.Rvz(0x30000, 1, 5, 22, 131072, false, 100);
        var proof = new RomRepresentationVerification(Guid.NewGuid(), Hash('b'), 512, new RomDecoderIdentity.Managed("reader"),
            "checked-output/test", "verifier", DateTimeOffset.UnixEpoch, [new(selector, logical)], observed);
        Should.Throw<ArgumentException>(() => new RomRepresentationFacts(1, 1, Hash('b'), 512, declared,
            RomReconstructionManifest.WholeFile.Instance, [new(1, 1, selector, logical)], [proof], []));
    }

    [Fact]
    public void Measurement_SameImmutableGraphWithNewDecoderAndTime_MatchesRetained()
    {
        var retained = Cd();
        Remeasure(retained).MatchesRetained(retained).ShouldBeTrue();
    }

    [Theory]
    [InlineData("renamed-bin")]
    [InlineData("renamed-cue")]
    [InlineData("cue-identity")]
    [InlineData("track-kind")]
    [InlineData("member-order")]
    [InlineData("container-parameters")]
    [InlineData("artifact-hash")]
    [InlineData("artifact-size")]
    public void Measurement_ChangedImmutableReconstruction_RefusesRetainedGraph(string mutation)
    {
        var retained = Cd();
        Remeasure(retained, mutation).MatchesRetained(retained).ShouldBeFalse();
    }

    private static RomMeasuredRepresentation Remeasure(RomRepresentationFacts retained, string? mutation = null)
    {
        var original = (RomReconstructionManifest.CdTracks)retained.Manifest;
        var members = original.Members.ToArray();
        var tracks = original.Tracks.ToArray();
        if (mutation == "renamed-bin")
        {
            var bin = (RomCdBinMember)members[0];
            members[0] = new RomCdBinMember("renamed.bin", bin.Identity, bin.Identity, bin.Tracks);
        }
        if (mutation == "renamed-cue")
        {
            var cue = (RomCdStoredMember)members[2];
            members[2] = new RomCdStoredMember("renamed.cue", cue.Identity, cue.Role);
        }
        if (mutation == "cue-identity")
            members[2] = new RomCdStoredMember("disc.cue", new(10, Hash('d'), ShortHash('d'), Md5.Parse(new string('d', 32)), Crc32.FromUInt32(0)), RomCdStoredRole.Cue);
        if (mutation == "track-kind")
            tracks[0] = tracks[0] with { Kind = RomCdTrackKind.Mode1Raw };
        if (mutation == "member-order")
            (members[0], members[1]) = (members[1], members[0]);
        var manifest = new RomReconstructionManifest.CdTracks(original.LogicalFrames, tracks, members);
        var prior = (RomContainerFacts.Chd)retained.Container;
        var container = mutation == "container-parameters" ? new RomContainerFacts.Chd(prior.Version, prior.LogicalBytes, 8 * 2448, prior.UnitBytes, 5,
            prior.Codecs, prior.HasParent, prior.StoredRawSha1, prior.StoredOverallSha1, prior.Metadata) : prior;
        var outputs = members.Select((member, index) => (member, index)).Where(pair => pair.member is RomCdBinMember).Select(pair =>
            new RomMeasuredOutput(new RomOutputSelector.Member(pair.index), new(pair.member.Identity.Size, pair.member.Identity.Sha256,
                pair.member.Identity.Sha1, pair.member.Identity.Md5, pair.member.Identity.Crc32))).ToArray();
        var proof = new RomRepresentationVerification(Guid.NewGuid(), mutation == "artifact-hash" ? Hash('a') : retained.ArtifactSha256,
            mutation == "artifact-size" ? 1024 : retained.ArtifactSize, new RomDecoderIdentity.Native(new("linux-x64", ShortHash('d'), 9)),
            "complete-new/v1", "new-verifier", DateTimeOffset.UnixEpoch.AddTicks(789), outputs, container, container.StoredRawSha1, container.StoredOverallSha1);
        return new(container, manifest, proof);
    }

    private static RomRepresentationFacts Cd(string? mutation = null)
    {
        var first = Identity(17 * 2352, 'a');
        var second = Identity(13 * 2352, 'b');
        var cue = Identity(10, 'c');
        static RomCdFileIdentity MemberIdentity(RomLogicalIdentity identity) =>
            new(identity.Size, identity.Sha256, identity.Sha1, identity.Md5, identity.Crc32);
        var firstMember = MemberIdentity(first);
        var secondMember = MemberIdentity(second);
        var manifest = new RomReconstructionManifest.CdTracks(36, [new(1, 0, 17, RomCdTrackKind.Mode2Raw), new(2, 20, 13, RomCdTrackKind.Audio)],
            [new RomCdBinMember("a.bin",firstMember,firstMember,[1]),new RomCdBinMember("b.bin",secondMember,secondMember,[2]),
             new RomCdStoredMember("disc.cue",MemberIdentity(cue),RomCdStoredRole.Cue)]);
        var container = new RomContainerFacts.Chd(5, 36 * 2448, 4 * 2448, 2448, 9, [0, 0, 0, 0], false, ShortHash('d'), ShortHash('e'),
            [new RomChdMetadataEntry(0x43485432, 1, "TRACK:1 TYPE:MODE2_RAW"u8)]);
        RomRepresentationOutput[] outputs = [new(10, 1, new RomOutputSelector.Member(0), first), new(11, 2, new RomOutputSelector.Member(1), second)];
        if (mutation == "descriptor-as-output")
            outputs[1] = new(11, 2, new RomOutputSelector.Member(2), cue);
        RomMeasuredOutput[] measured = [new(new RomOutputSelector.Member(0), first), new(new RomOutputSelector.Member(1), second)];
        if (mutation == "partial-proof")
            measured = [measured[0]];
        if (mutation == "descriptor-proof")
            measured[1] = new(new RomOutputSelector.Member(2), cue);
        RomRepresentationVerification Proof(string rid) => new(Guid.NewGuid(), Hash(mutation == "artifact" ? 'e' : 'f'), 512, Native(rid),
            "checked-cd/test", "verifier", DateTimeOffset.UnixEpoch, measured, container, ShortHash(mutation == "integrity" ? 'f' : 'd'), ShortHash('e'));
        RomRepresentationVerification[] proofs = mutation == "missing-proof" ? [] :
            [Proof("linux-x64"), Proof(mutation == "duplicate-build" ? "linux-x64" : "linux-arm64")];
        RomRetainedDescriptor[] descriptors = mutation == "missing-descriptor" ? [] : [new(2, 30, Hash(mutation == "wrong-descriptor" ? 'f' : 'c'), 10)];
        return new(1, 1, Hash('f'), 512, container, manifest, outputs, proofs, descriptors);
    }
}
