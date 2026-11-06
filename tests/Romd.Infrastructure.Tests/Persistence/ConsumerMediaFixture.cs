using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Source.Rom;
using Romd.Application.Common.Content;
using Romd.Domain.Source.Rom;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.MediaFacts;
using Romd.PostgreSql.TestSupport;

namespace Romd.Infrastructure.Tests.Persistence;

/// <summary>Publishes explicit current whole-file fixtures through the guarded fresh model.</summary>
internal static class ConsumerMediaFixture
{
    internal const string ManagedBuild = "consumer-fixture-managed/v1";
    internal static async Task<RomContentArtifact> PublishAsync(RomdDbContext db, int romId, int fileId, RomContainerFacts container,
        RomDecoderIdentity? decoder, bool preferred, DateTimeOffset observedAt)
    {
        var logical = await db.RomFiles.SingleAsync(row => row.Id == romId);
        var identity = new RomLogicalIdentity(logical.ContentSize, logical.ContentSha256, logical.Sha1, logical.Md5, logical.Crc32);
        var file = await db.Files.SingleAsync(row => row.Id == fileId);
        await using var transaction = await db.Database.BeginTransactionAsync();
        RomRepresentationFacts facts;
        if (decoder is not null)
        {
            var proof = MediaFactFixtures.WholeMeasurement(Guid.NewGuid(), file.Sha256, file.Size, identity, container, decoder,
                "complete-whole-output/v1", "consumer-fixture-verifier", observedAt,
                (container as RomContainerFacts.Chd)?.StoredRawSha1, (container as RomContainerFacts.Chd)?.StoredOverallSha1);
            facts = await db.StageMeasuredWholeAsync(fileId, container, proof, new(RomOutputSelector.Whole.Instance, romId, preferred), CancellationToken.None);
        }
        else
        {
            facts = await db.StageUnmeasuredWholeAsync(fileId, identity, container, romId, preferred, CancellationToken.None);
        }
        await transaction.CommitAsync();
        db.ChangeTracker.Clear();
        return new(facts, facts.Outputs.Single().Id, preferred, file.ArtifactMd5, file.ArtifactSha1);
    }
    internal static RomContentDescriptor ForeignSnapshot(RomContentDescriptor source, string changed)
    {
        int romId = changed == "owner" ? 999 : source.RomId;
        var old = source.Identity;
        var identity = new RomLogicalIdentity(changed == "logical-size" ? old.Size + 2048 : old.Size,
            changed == "logical-sha256" ? Romd.Domain.Hashing.Sha256.Parse(new string('9', 64)) : old.Sha256,
            changed == "logical-sha1" ? Romd.Domain.Hashing.Sha1.Parse(new string('9', 40)) : old.Sha1,
            changed == "logical-md5" ? Romd.Domain.Hashing.Md5.Parse(new string('9', 32)) : old.Md5,
            changed == "logical-crc" ? Romd.Domain.Hashing.Crc32.FromUInt32(9) : old.Crc32);
        int preferred = changed == "encoding" ? source.Artifacts.Single(artifact => artifact.Encoding == RomRepresentationEncoding.Identity).OutputId : source.OutputId;
        var artifacts = source.Artifacts.Select(artifact =>
        {
            bool isChanged = artifact.IsPreferred;
            RomContainerFacts container = artifact.Container switch
            {
                RomContainerFacts.Identity => RomContainerFacts.Identity.Instance,
                RomContainerFacts.Chd chd => new RomContainerFacts.Chd(chd.Version, identity.Size, chd.HunkBytes, chd.UnitBytes,
                    checked((uint)((identity.Size + chd.HunkBytes - 1) / chd.HunkBytes)), chd.Codecs, chd.HasParent, identity.Sha1, chd.StoredOverallSha1, chd.Metadata),
                RomContainerFacts.Rvz rvz => new RomContainerFacts.Rvz(rvz.Version, rvz.DiscType, rvz.Compression, rvz.CompressionLevel, rvz.BlockBytes, rvz.HasWiiPartitions, identity.Size),
                _ => throw new InvalidOperationException("This fixture changes a whole-file projection.")
            };
            var sha = container is RomContainerFacts.Identity ? identity.Sha256 : changed == "artifact-sha256" && isChanged ? Romd.Domain.Hashing.Sha256.Parse(new string('9', 64)) : artifact.ArtifactSha256;
            long size = container is RomContainerFacts.Identity ? identity.Size : changed == "artifact-size" && isChanged ? artifact.ArtifactSize + 1 : artifact.ArtifactSize;
            int fileId = changed == "file" && isChanged ? 999 : artifact.FileId;
            int repId = changed == "representation" && isChanged ? 999 : artifact.RepresentationId;
            var proofs = artifact.Facts.Verifications.Select(proof => new RomRepresentationVerification(Guid.NewGuid(), sha, size, proof.Decoder, proof.CheckedProfile,
                proof.VerifierBuild, proof.ObservedAt, [new(RomOutputSelector.Whole.Instance, identity)], container,
                (container as RomContainerFacts.Chd)?.StoredRawSha1, (container as RomContainerFacts.Chd)?.StoredOverallSha1)).ToArray();
            var facts = new RomRepresentationFacts(repId, fileId, sha, size, container, RomReconstructionManifest.WholeFile.Instance,
                [new(artifact.OutputId, romId, RomOutputSelector.Whole.Instance, identity)], proofs, []);
            return new RomContentArtifact(facts, artifact.OutputId, artifact.OutputId == preferred, artifact.ArtifactMd5, artifact.ArtifactSha1);
        }).ToArray();
        return new(romId, preferred, identity, source.OriginalFilename, artifacts, [], []);
    }

}

internal sealed class ConsumerMediaAdmission(string managedBuild, RomChdDecoderBuild? decoder) : IRomChdDeliveryAdmission
{
    internal bool Available { get; set; } = true;
    public RomChdDeliveryAssessment? Assess(RomChdDeliveryEvidence evidence) => Available && decoder is not null ? new(decoder) : null;
    public RomManagedReadBinding? AssessManaged(RomContentArtifact artifact) => Available && artifact.Container is RomContainerFacts.Rvz &&
        artifact.Facts.Verifications.Any(proof => proof.Decoder is RomDecoderIdentity.Managed managed && managed.Build == managedBuild) ? artifact.BindManaged(managedBuild) : null;
    public bool AdmitsCd(RomReconstruction.CdMember member) => AssessCd(member) is not null;
    public RomChdDecoderBuild? AssessCd(RomReconstruction.CdMember member) => Available && decoder is not null && member.HasDecoder(decoder) ? decoder : null;
}
