using NSubstitute;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Storage.Files;
using Shouldly;
using Xunit;
using Romd.Application.Tests.Content;
using Romd.Domain.Storage;

namespace Romd.Application.Tests.Source.Rom;

public sealed class RomCdArchivePublisherTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PublishAsync_FileBatch_FlushesPendingIdentitiesBeforeChecksumsAndPreservesKeep(bool pending, bool keep)
    {
        var retained = DeliveryFacts.CdContent().Preferred.Facts;
        var manifest = (RomReconstructionManifest.CdTracks)retained.Manifest;
        var measured = new RomMeasuredRepresentation(retained.Container, manifest, retained.Verifications.Single());
        var chd = Substitute.For<ITempFile>();
        chd.FileSha256.Returns(retained.ArtifactSha256);
        chd.FileSize.Returns(retained.ArtifactSize);
        chd.DatHashes.Returns(new Romd.Admin.Application.Hashing.DatHashResult(DeliveryFacts.Short('b'), DeliveryFacts.Md5Value('b'),
            Crc32.FromUInt32(1), retained.ArtifactSize));
        var stored = manifest.Members.Select((member, ordinal) => (member, ordinal)).Where(pair => pair.member is RomCdStoredMember).ToArray();
        var descriptors = stored.ToDictionary(pair => pair.member.Path, pair =>
        {
            var file = Substitute.For<ITempFile>();
            file.FileSha256.Returns(pair.member.Identity.Sha256);
            file.FileSize.Returns(pair.member.Identity.Size);
            return file;
        });
        var events = new List<string>();
        var files = Substitute.For<IFileStorageService>();
        var stagedChd = Substitute.For<IStagedFile>();
        files.StageRawAsync(chd, default).Returns(stagedChd);
        files.StageAsync(Arg.Any<ITempFile>(), default).Returns(_ => Substitute.For<IStagedFile>());
        var batch = Substitute.For<IStagedFilePublicationBatch>();
        batch.RequiresFlush.Returns(pending);
        var results = new[] { new FileStoreResult(FileEntity.Rehydrate(retained.FileId, retained.ArtifactSha256, retained.ArtifactSize,
            retained.ArtifactSize, false, DateTimeOffset.UnixEpoch), !keep, false) }.Concat(stored.Select(pair =>
                new FileStoreResult(FileEntity.Rehydrate(retained.Descriptors.Single(row => row.Ordinal == pair.ordinal).FileId,
                    pair.member.Identity.Sha256, pair.member.Identity.Size, pair.member.Identity.Size, false, DateTimeOffset.UnixEpoch), false, true))).ToArray();
        batch.ReadAfterFlush().Returns(_ => { events.Add("read"); return results; });
        files.PublishStagedAsync(Arg.Any<IReadOnlyList<IStagedFile>>(), default).Returns(_ => { events.Add("publish"); return batch; });
        files.When(value => value.RecordArtifactChecksumsAsync(Arg.Any<int>(), Arg.Any<TempFileArtifactMeasurement>(), default)).Do(_ => events.Add("checksums"));
        var gate = Substitute.For<IRomCdArchivePublicationGate>();
        var owners = retained.Outputs.ToDictionary(output => manifest.Members[((RomOutputSelector.Member)output.Selector).Ordinal].Path, output => output.RomId!.Value);
        gate.PrepareAsync(measured, default).Returns(keep ? new RomCdArchivePublicationAuthority.Keep(retained)
            : new RomCdArchivePublicationAuthority.Publish(owners, retained));
        gate.CompleteAsync(retained, default).Returns(_ => { events.Add("complete"); return Task.FromResult<Romd.Domain.Jobs.JobItem?>(null); });
        var facts = Substitute.For<IMediaFactPublisher>();
        var work = Substitute.For<IUnitOfWork>();
        var transaction = Substitute.For<ITransaction>();
        work.BeginTransactionAsync(default).Returns(transaction);
        work.When(value => value.FlushAsync(default)).Do(_ => events.Add("flush"));
        transaction.When(value => value.CommitAsync(default)).Do(_ => events.Add("commit"));

        (await new RomCdArchivePublisher(files, work, facts, Substitute.For<IJobItemRepository>())
            .PublishAsync(measured, chd, descriptors, gate)).ShouldBeSameAs(retained);

        events.ShouldBe(keep ? ["complete", "flush", "commit"] : pending
            ? ["publish", "flush", "read", "checksums", "complete", "flush", "commit"]
            : ["publish", "read", "checksums", "complete", "flush", "commit"]);
    }

    [Fact]
    public async Task PublishAsync_CleanupFailure_ReleasesEveryStageAndKeepsOriginalFailure()
    {
        var manifest = RomCdManifestTests.Manifest();
        var raw = Sha1.Parse(new string('e', 40));
        var overall = Sha1.Parse(new string('f', 40));
        var artifact = Sha256.Parse(new string('d', 64));
        var container = new RomContainerFacts.Chd(5, 36 * 2448, 19584, 2448, 5, [0x63646c7a, 0, 0, 0], false, raw, overall,
            [new(0x43485432,1,"TRACK:1 TYPE:MODE2_RAW SUBTYPE:NONE FRAMES:17"u8),
             new(0x43485432,1,"TRACK:2 TYPE:AUDIO SUBTYPE:NONE FRAMES:13"u8)]);
        var bin = manifest.Members[0].Identity;
        var proof = new RomRepresentationVerification(Guid.NewGuid(), artifact, 512,
            new RomDecoderIdentity.Native(new("linux-x64", Sha1.Parse(new string('a', 40)), 1)), "complete-cd/v1", "fixture-verifier", DateTimeOffset.UnixEpoch,
            [new(new RomOutputSelector.Member(0), new(bin.Size, bin.Sha256, bin.Sha1, bin.Md5, bin.Crc32))], container, raw, overall);
        var measured = new RomMeasuredRepresentation(container, manifest, proof);
        var gate = Substitute.For<IRomCdArchivePublicationGate>();
        gate.PrepareAsync(measured, default).Returns(new RomCdArchivePublicationAuthority.Publish(new Dictionary<string, int> { ["disc.bin"] = 1 }, retained: null));
        var chd = Substitute.For<ITempFile>();
        chd.FileSha256.Returns(artifact);
        chd.FileSize.Returns(512);
        chd.DatHashes.Returns(new Romd.Admin.Application.Hashing.DatHashResult(raw, Md5.Parse(new string('c', 32)), Crc32.FromUInt32(123), 512));
        var cue = Substitute.For<ITempFile>();
        cue.FileSha256.Returns(manifest.Members[1].Identity.Sha256);
        cue.FileSize.Returns(manifest.Members[1].Identity.Size);
        var stagedChd = Substitute.For<IStagedFile>();
        var stagedCue = Substitute.For<IStagedFile>();
        stagedCue.DisposeAsync().Returns(_ => throw new InvalidOperationException("cleanup failed"));
        var files = Substitute.For<IFileStorageService>();
        files.StageRawAsync(chd, default).Returns(stagedChd);
        files.StageAsync(cue, default).Returns(stagedCue);
        var work = Substitute.For<IUnitOfWork>();
        work.BeginTransactionAsync(default).Returns<Task<ITransaction>>(_ => throw new IOException("transaction failed"));
        var publisher = new RomCdArchivePublisher(files, work, Substitute.For<IMediaFactPublisher>(), Substitute.For<IJobItemRepository>());
        var error = await Should.ThrowAsync<AggregateException>(() => publisher.PublishAsync(measured, chd, new Dictionary<string, ITempFile> { ["disc.cue"] = cue }, gate));
        error.InnerExceptions[0].ShouldBeOfType<IOException>().Message.ShouldBe("transaction failed");
        error.InnerExceptions[1].ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("cleanup failed");
        await stagedChd.Received(1).DisposeAsync();
        await chd.DidNotReceive().DisposeAsync();
        await cue.DidNotReceive().DisposeAsync();
    }
}
