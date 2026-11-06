using System.Buffers.Binary;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Romd.Application.Common.Content;
using Romd.Consumer.Application.Libraries;
using Romd.Consumer.Application.Vfs;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Persistence.MediaFacts;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class ConsumerVfsRepositoryTests
{
    [Theory]
    [InlineData(1, 1, new[] { 2 })]
    [InlineData(8, 1, new[] { 16 })]
    [InlineData(129, 1, new[] { 256, 2 })]
    [InlineData(257, 2, new[] { 256, 256, 2 })]
    public async Task ListReleasesAsync_MultipleChdRoms_ReadsEvidenceByBatch(int romCount, int batches, int[] graphSizes)
    {
        var evidence = await SeedPs2ChdAsync();
        await SeedAdditionalPs2ChdsAsync(romCount, evidence);
        var cost = new VfsQueryCost();
        await using var db = ObservedDb(cost);
        var repository = new ConsumerVfsRepository(db, DeliveryPolicy, MediaTestServices.CreateContentCatalog(db), chdAdmission: new SyntheticChdAdmission(NativeDecoder));

        var listed = (await repository.ListReleasesAsync(new(UserId), PlatformId))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsReleasePage>.Found>().Value;

        listed.ReleasePaths[ReleaseId].Count.ShouldBe(romCount);
        listed.ReleasePaths[ReleaseId].ShouldAllBe(path => path.EndsWith(".chd", StringComparison.Ordinal));
        // Eight projection reads, five per logical catalog batch, and fifteen per complete representation batch.
        cost.Commands.ShouldBe(8 + 5 * batches + 15 * graphSizes.Length);
        cost.GraphBatchSizes.ShouldBe(graphSizes);
        cost.PathFactReads.ShouldHaveSingleItem().ShouldBe(romCount + 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FindByRomIdsAsync_ValidEvidence_MatchesSingleLookupAndOmitsUnknownIds(bool chd)
    {
        if (chd)
            await SeedPs2ChdAsync();
        else
            SeedReleaseFiles(("plain.bin", true));
        var cost = new VfsQueryCost();
        await using var db = ObservedDb(cost);
        var catalog = Romd.PostgreSql.TestSupport.MediaTestServices.CreateContentCatalog(db);
        var single = (await catalog.FindByRomIdAsync(1)).ShouldNotBeNull();
        cost.Reset();

        var batch = await catalog.FindByRomIdsAsync([1, 1, 999]);

        batch.Keys.ShouldBe([1]);
        AssertSameWholeFacts(batch[1], single);
        if (chd)
            batch[1].Artifacts!.Single(artifact => artifact.Encoding == RomRepresentationEncoding.Chd)
            .ChdEvidence.ShouldNotBeNull().ShouldHaveSingleItem();
        cost.Commands.ShouldBe(20);
    }

    [Fact]
    public async Task ListReleasesAsync_DecoderAdmissionWithdrawn_ReassessesOnEveryRequest()
    {
        var evidence = await SeedPs2ChdAsync();
        await using var db = CreateDb();
        var admission = new RevocableChdAdmission(NativeDecoder);
        var repository = new ConsumerVfsRepository(db, DeliveryPolicy, MediaTestServices.CreateContentCatalog(db), chdAdmission: admission);
        (await repository.ListReleasesAsync(new(UserId), PlatformId))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsReleasePage>.Found>().Value
            .ReleasePaths[ReleaseId].ShouldBe(["folder/disc.chd"]);

        admission.Available = false;

        (await repository.ListReleasesAsync(new(UserId), PlatformId))
            .ShouldBeOfType<ConsumerLibraryReadResult<ConsumerVfsReleasePage>.Found>().Value
            .ReleasePaths[ReleaseId].ShouldBe(["folder/disc.iso"]);
    }

    [Theory]
    [InlineData("missing-parameters")]
    [InlineData("contradictory-raw-hash")]
    [InlineData("blank-verifier")]
    public async Task FindByRomIdsAsync_IncompleteOrContradictoryEvidence_RejectsExactlyLikeSingleLookup(string corruption)
    {
        await SeedPs2ChdAsync();
        var corrupt = new CorruptChdEvidence(corruption);
        await using var db = ObservedDb(corrupt);
        var catalog = Romd.PostgreSql.TestSupport.MediaTestServices.CreateContentCatalog(db);

        await Should.ThrowAsync<InvalidDataException>(() => catalog.FindByRomIdAsync(1));
        await Should.ThrowAsync<InvalidDataException>(() => catalog.FindByRomIdsAsync([1]));
        corrupt.RowsMutated.ShouldBe(2);

    }

    [Fact]
    public async Task FindByRomIdsAsync_EmptyOrExcessiveBatch_DoesNotQueryDatabase()
    {
        var cost = new VfsQueryCost();
        await using var db = ObservedDb(cost);
        var catalog = Romd.PostgreSql.TestSupport.MediaTestServices.CreateContentCatalog(db);

        (await catalog.FindByRomIdsAsync([])).ShouldBeEmpty();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            catalog.FindByRomIdsAsync(Enumerable.Range(1, IRomContentCatalog.MaximumBatchSize + 1).ToArray()));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => catalog.FindByRomIdsAsync([0]));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.FindByRomIdsAsync([1], cancelled.Token));
        cost.Commands.ShouldBe(0);
    }

    private static void AssertSameWholeFacts(RomContentDescriptor actual, RomContentDescriptor expected)
    {
        actual.RomId.ShouldBe(expected.RomId);
        actual.OutputId.ShouldBe(expected.OutputId);
        actual.Identity.ShouldBe(expected.Identity);
        actual.OriginalFilename.ShouldBe(expected.OriginalFilename);
        actual.Artifacts.Count.ShouldBe(expected.Artifacts.Count);
        actual.Inspections.ShouldBeEmpty();
        expected.Inspections.ShouldBeEmpty();
        actual.References.ShouldBeEmpty();
        expected.References.ShouldBeEmpty();
        foreach (var artifact in actual.Artifacts)
        {
            var other = expected.Artifacts.Single(item => item.OutputId == artifact.OutputId);
            artifact.IsPreferred.ShouldBe(other.IsPreferred);
            artifact.FileId.ShouldBe(other.FileId);
            artifact.RepresentationId.ShouldBe(other.RepresentationId);
            artifact.ArtifactSha256.ShouldBe(other.ArtifactSha256);
            artifact.ArtifactSize.ShouldBe(other.ArtifactSize);
            artifact.ArtifactMd5.ShouldBe(other.ArtifactMd5);
            artifact.ArtifactSha1.ShouldBe(other.ArtifactSha1);
            artifact.Container.Encoding.ShouldBe(other.Container.Encoding);
            artifact.Container.Fingerprint.ShouldBe(other.Container.Fingerprint);
            if (artifact.Container is RomContainerFacts.Chd chd)
            {
                var header = other.Container.ShouldBeOfType<RomContainerFacts.Chd>();
                chd.Version.ShouldBe(header.Version);
                chd.LogicalBytes.ShouldBe(header.LogicalBytes);
                chd.HunkBytes.ShouldBe(header.HunkBytes);
                chd.UnitBytes.ShouldBe(header.UnitBytes);
                chd.HunkCount.ShouldBe(header.HunkCount);
                chd.Codecs.ShouldBe(header.Codecs);
                chd.HasParent.ShouldBe(header.HasParent);
                chd.StoredRawSha1.ShouldBe(header.StoredRawSha1);
                chd.StoredOverallSha1.ShouldBe(header.StoredOverallSha1);
                chd.Metadata.Select(entry => (entry.Tag, entry.Flags, Convert.ToHexString(entry.Payload))).ShouldBe(
                    header.Metadata.Select(entry => (entry.Tag, entry.Flags, Convert.ToHexString(entry.Payload))));
            }
            artifact.Facts.Manifest.ShouldBeOfType<RomReconstructionManifest.WholeFile>();
            other.Facts.Manifest.ShouldBeOfType<RomReconstructionManifest.WholeFile>();
            artifact.Facts.Outputs.ShouldBe(other.Facts.Outputs);
            artifact.Facts.Descriptors.ShouldBe(other.Facts.Descriptors);
            artifact.Facts.Creations.ShouldBeEmpty();
            other.Facts.Creations.ShouldBeEmpty();
            artifact.Facts.CdBootInspections.ShouldBeEmpty();
            other.Facts.CdBootInspections.ShouldBeEmpty();
            artifact.Facts.Verifications.Count.ShouldBe(other.Facts.Verifications.Count);
            foreach (var proof in artifact.Facts.Verifications)
            {
                var held = other.Facts.Verifications.Single(item => item.Id == proof.Id);
                proof.Decoder.ShouldBe(held.Decoder);
                proof.ArtifactSha256.ShouldBe(held.ArtifactSha256);
                proof.ArtifactSize.ShouldBe(held.ArtifactSize);
                proof.CheckedProfile.ShouldBe(held.CheckedProfile);
                proof.VerifierBuild.ShouldBe(held.VerifierBuild);
                proof.ObservedAt.ShouldBe(held.ObservedAt);
                proof.ObservedParametersSha256.ShouldBe(held.ObservedParametersSha256);
                proof.ComputedRawSha1.ShouldBe(held.ComputedRawSha1);
                proof.ComputedOverallSha1.ShouldBe(held.ComputedOverallSha1);
                proof.Outputs.ShouldBe(held.Outputs);
            }
            artifact.ChdEvidence.Select(item => item.Bind()).ShouldBe(other.ChdEvidence.Select(item => item.Bind()));
        }
    }

    private async Task SeedAdditionalPs2ChdsAsync(int romCount, RomContentArtifact template)
    {
        await using var db = CreateDb();
        var captured = (RomContainerFacts.Chd)template.Container;
        for (int id = 2; id <= romCount; id++)
        {
            var logical = NumberedHash<Sha256>(id, 1);
            var sha1 = NumberedHash<Sha1>(id, 1);
            var md5 = NumberedHash<Md5>(id, 1);
            var crc = Crc32.FromUInt32((uint)id);
            db.Files.AddRange(new FileEntityPersistence { Id = 2 * id - 1, Sha256 = logical, Size = 8192, SizeOnDisk = 8192 },
                new FileEntityPersistence
                {
                    Id = 2 * id,
                    Sha256 = NumberedHash<Sha256>(id, 2),
                    Size = 4096,
                    SizeOnDisk = 4096,
                    ArtifactMd5 = NumberedHash<Md5>(id, 2),
                    ArtifactSha1 = NumberedHash<Sha1>(id, 2)
                });
            db.StageOriginalRom(new Romd.PostgreSql.TestSupport.OriginalRomFixture { Id = id, FileId = 2 * id - 1, OriginalFilename = $"disc-{id}.iso", Sha1 = sha1, Md5 = md5, Crc32 = crc });
            await db.PublishOriginalRomsAsync();
            var container = new RomContainerFacts.Chd(5, 8192, captured.HunkBytes, 2048, captured.HunkCount, captured.Codecs, false, sha1, NumberedHash<Sha1>(id, 3), captured.Metadata);
            await ConsumerMediaFixture.PublishAsync(db, id, 2 * id, container, new RomDecoderIdentity.Native(NativeDecoder), true, template.Facts.Verifications.Single().ObservedAt);
            db.CatalogReleaseFiles.Add(new CatalogReleaseFileEntity { CatalogReleaseId = ReleaseId, FileFingerprint = $"disc-{id}", Name = $"disc-{id}.iso", Size = 8192, Sha1 = sha1 });
            await db.SaveChangesAsync();
        }
    }

    private static T NumberedHash<T>(int id, byte kind) where T : struct, IHashValue<T>
    {
        var bytes = new byte[T.ByteLength];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, id);
        bytes[^1] = kind;
        return T.FromSpan(bytes);
    }

    private sealed class CorruptChdEvidence(string corruption) : IMaterializationInterceptor
    {
        public int RowsMutated { get; private set; }
        public object InitializedInstance(MaterializationInterceptionData data, object entity)
        {
            // The complete reader materializes SQL-owned projection rows, not the retired EF integrity aggregate.
            var type = entity.GetType();
            if (type.Name != "ProofRow" || type.GetProperty("DecoderRid")?.GetValue(entity) is null)
                return entity;
            string property = corruption switch { "missing-parameters" => "ObservedParametersSha256", "contradictory-raw-hash" => "ComputedRawSha1", _ => "VerifierBuild" };
            object? value = corruption switch { "missing-parameters" => null, "contradictory-raw-hash" => NewSha1(9).ToArray(), _ => " " };
            type.GetProperty(property).ShouldNotBeNull().SetValue(entity, value);
            RowsMutated++;
            return entity;
        }
    }

    private sealed class RevocableChdAdmission(RomChdDecoderBuild decoder) : IRomChdDeliveryAdmission
    {
        private readonly ConsumerMediaAdmission _inner = new(ConsumerMediaFixture.ManagedBuild, decoder);
        public bool Available { get => _inner.Available; set => _inner.Available = value; }
        public RomChdDeliveryAssessment? Assess(RomChdDeliveryEvidence evidence) => _inner.Assess(evidence);
        public RomManagedReadBinding? AssessManaged(RomContentArtifact artifact) => _inner.AssessManaged(artifact);
        public bool AdmitsCd(RomReconstruction.CdMember member) => _inner.AdmitsCd(member);
        public RomChdDecoderBuild? AssessCd(RomReconstruction.CdMember member) => _inner.AssessCd(member);
    }
}
