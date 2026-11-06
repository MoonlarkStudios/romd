using Romd.Application.Common.Content;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;

namespace Romd.Application.Tests.Content;

/// <summary>Complete synthetic retained graphs; these are not decoder or real-media qualification.</summary>
internal static class DeliveryFacts
{
    internal const string ManagedBuild = "delivery-fixture-managed/v1";
    internal static readonly DateTimeOffset ObservedAt = DateTimeOffset.UnixEpoch.AddTicks(47);
    internal static Sha256 Hash(char value) => Sha256.Parse(new string(value, 64));
    internal static Sha1 Short(char value) => Sha1.Parse(new string(value, 40));
    internal static Md5 Md5Value(char value) => Md5.Parse(new string(value, 32));
    internal static RomLogicalIdentity Identity(long size) => new(size, Hash('a'), Short('a'), Md5Value('a'), Crc32.Parse("aaaaaaaa"));
    internal static RomChdDecoderBuild Decoder(int recipe = 1) => new("linux-x64", Short('f'), recipe);

    internal static RomContentArtifact Original(int romId, int fileId, int representationId, int outputId, RomLogicalIdentity identity, bool preferred)
    {
        var facts = Measured(romId, fileId, representationId, outputId, identity, identity.Sha256, identity.Size, RomContainerFacts.Identity.Instance,
            new RomDecoderIdentity.Managed(ManagedBuild));
        return new(facts, outputId, preferred, identity.Md5, identity.Sha1);
    }
    internal static RomContentArtifact Rvz(int romId, int fileId, int representationId, int outputId, RomLogicalIdentity identity, Sha256 artifactHash,
        long artifactSize, uint discType, bool preferred, Md5? physicalMd5, Sha1? physicalSha1)
    {
        var container = new RomContainerFacts.Rvz(0x01000000, discType, 5, 19, 131072, discType == 2, identity.Size);
        var facts = Measured(romId, fileId, representationId, outputId, identity, artifactHash, artifactSize, container, new RomDecoderIdentity.Managed(ManagedBuild));
        return new(facts, outputId, preferred, physicalMd5, physicalSha1);
    }
    internal static RomContentArtifact Chd(int romId, int fileId, int representationId, int outputId, RomLogicalIdentity identity, Sha256 artifactHash,
        long artifactSize, RomChdDecoderBuild decoder, uint hunkBytes, uint codec, bool preferred, Md5? physicalMd5, Sha1? physicalSha1)
    {
        var container = new RomContainerFacts.Chd(5, identity.Size, hunkBytes, 2048, checked((uint)((identity.Size + hunkBytes - 1) / hunkBytes)),
            [codec, 0, 0, 0], false, identity.Sha1, Short('c'), [new(0x44564420, 1, [0])]);
        var facts = Measured(romId, fileId, representationId, outputId, identity, artifactHash, artifactSize, container, new RomDecoderIdentity.Native(decoder));
        return new(facts, outputId, preferred, physicalMd5, physicalSha1);
    }
    private static RomRepresentationFacts Measured(int romId, int fileId, int representationId, int outputId, RomLogicalIdentity identity,
        Sha256 artifactHash, long artifactSize, RomContainerFacts container, RomDecoderIdentity decoder)
    {
        var proof = new RomRepresentationVerification(Guid.NewGuid(), artifactHash, artifactSize, decoder, "complete-output/v1", "delivery-fixture-verifier",
            ObservedAt, [new(RomOutputSelector.Whole.Instance, identity)], container,
            (container as RomContainerFacts.Chd)?.StoredRawSha1, (container as RomContainerFacts.Chd)?.StoredOverallSha1);
        return new(representationId, fileId, artifactHash, artifactSize, container, RomReconstructionManifest.WholeFile.Instance,
            [new(outputId, romId, RomOutputSelector.Whole.Instance, identity)], [proof], []);
    }
    internal static RomContentArtifact WithoutProof(RomContentArtifact artifact)
    {
        var facts = artifact.Facts;
        return new(new(facts.Id, facts.FileId, facts.ArtifactSha256, facts.ArtifactSize, facts.Container, facts.Manifest, facts.Outputs, [], facts.Descriptors),
            artifact.OutputId, artifact.IsPreferred, artifact.ArtifactMd5, artifact.ArtifactSha1);
    }
    internal static RomContentArtifact Select(RomContentArtifact artifact, bool preferred, Md5? physicalMd5, Sha1? physicalSha1) =>
        new(artifact.Facts, artifact.OutputId, preferred, physicalMd5, physicalSha1);
    internal static RomContentDescriptor OriginalContent(int romId, int fileId, long size)
    {
        var identity = Identity(size);
        var artifact = Original(romId, fileId, fileId, fileId, identity, true);
        return new(romId, artifact.OutputId, identity, "disc.iso", [artifact], [], []);
    }
    internal static RomContentDescriptor RvzContent(int romId, int fileId, long size, long artifactSize)
    {
        var identity = Identity(size);
        var artifact = Rvz(romId, fileId, fileId, fileId, identity, Hash('b'), artifactSize, 1, true, Md5Value('b'), Short('b'));
        return new(romId, artifact.OutputId, identity, "disc.iso", [artifact], [], []);
    }
    internal static RomContentDescriptor ChdContent(int romId, int fileId)
    {
        var identity = Identity(8192);
        var artifact = Chd(romId, fileId, fileId, fileId, identity, Hash('b'), 4096, Decoder(), 4096, RomChdCodecs.Zlib, true, Md5Value('b'), Short('b'));
        return new(romId, artifact.OutputId, identity, "disc.iso", [artifact], [], []);
    }
    internal static RomContentDescriptor CdContent()
    {
        const int frames = 128;
        var identity = Identity(frames * 2352);
        var cue = new RomCdFileIdentity(98, Hash('d'), Short('d'), Md5Value('d'), Crc32.FromUInt32(2));
        var bin = new RomCdFileIdentity(identity.Size, identity.Sha256, identity.Sha1, identity.Md5, identity.Crc32);
        var container = new RomContainerFacts.Chd(5, frames * 2448, 4 * 2448, 2448, 32, [0, 0, 0, 0], false, Short('e'), Short('f'),
            [new(0x43485432, 1, "TRACK:1 TYPE:MODE2_RAW SUBTYPE:NONE FRAMES:128 PREGAP:0 PGTYPE:MODE2_RAW PGSUB:NONE POSTGAP:0"u8)]);
        var manifest = new RomReconstructionManifest.CdTracks(frames, [new(1, 0, frames, RomCdTrackKind.Mode2Raw)],
            [new RomCdBinMember("data.bin", bin, bin, [1]), new RomCdStoredMember("disc.cue", cue, RomCdStoredRole.Cue)]);
        var proof = new RomRepresentationVerification(Guid.NewGuid(), Hash('b'), 4096, new RomDecoderIdentity.Native(Decoder()), "complete-cd/v1",
            "delivery-fixture-verifier", ObservedAt, [new(new RomOutputSelector.Member(0), identity)], container, container.StoredRawSha1, container.StoredOverallSha1);
        var facts = new RomRepresentationFacts(42, 9, Hash('b'), 4096, container, manifest, [new(91, 1, new RomOutputSelector.Member(0), identity)], [proof], [new(1, 10, cue.Sha256, cue.Size)]);
        return new(1, 91, identity, "data.bin", [new(facts, 91, true, Md5Value('b'), Short('b'))], [], []);
    }
}

/// <summary>A serving authority configured for exact fixture decoder builds; every binding still selects retained proof.</summary>
internal sealed class DeliveryAdmission(string managedBuild, RomChdDecoderBuild? nativeDecoder) : IRomChdDeliveryAdmission
{
    internal RomChdDeliveryAssessment? NativeAssessment { get; set; } = nativeDecoder is null ? null : new(nativeDecoder);
    public RomChdDeliveryAssessment? Assess(RomChdDeliveryEvidence evidence) => NativeAssessment;
    public RomManagedReadBinding? AssessManaged(RomContentArtifact artifact) => artifact.Container is RomContainerFacts.Rvz &&
        artifact.Facts.Verifications.Any(proof => proof.Decoder is RomDecoderIdentity.Managed managed && managed.Build == managedBuild)
            ? artifact.BindManaged(managedBuild) : null;
    public bool AdmitsCd(RomReconstruction.CdMember member) => AssessCd(member) is not null;
    public RomChdDecoderBuild? AssessCd(RomReconstruction.CdMember member) => NativeAssessment is { Decoder: var decoder } && member.HasDecoder(decoder) ? decoder : null;
}
