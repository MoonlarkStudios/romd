using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Catalog.Commands.DecideOriginalDiscLayout;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Concurrency;
using Romd.Application.Common.Ids;
using Romd.Application.Common.Validation;
using Romd.Domain.Catalog;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Domain.ReferenceData;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Infrastructure.Tests.Storage;
using Romd.Media.TestSupport;
using Romd.Persistence;
using Romd.Persistence.Catalog;
using Romd.Persistence.Entities;
using Romd.Persistence.MediaFacts;
using Romd.Persistence.Repositories;
using Romd.Persistence.Repositories.Reconciliation;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class MediaReconciliationPublicationIntegrationTests
{
    [LinuxEncodingTheory]
    [InlineData("commit", false)]
    [InlineData("wrong-md5", false)]
    [InlineData("wrong-sha1", false)]
    [InlineData("commit", true)]
    [InlineData("wrong-md5", true)]
    [InlineData("wrong-sha1", true)]
    public async Task Cd_RealReservedOutput_PublishesMeasuredChecksumsAndFactsWithoutDatHashes(string publication, bool saturn)
    {
        await using var db = database.CreateContext();
        var work = new EfUnitOfWork(db);
        await using var media = new PublicationMedia(db, work, new NativeToolOptions
        {
            LibchdrDirectory = NativeTestBuilds.LibchdrDirectory,
            ChdmanDirectory = NativeTestBuilds.ChdmanDirectory,
        });
        byte[] data = saturn ? SyntheticSaturnCd.Raw() : SyntheticPs1Cd.ToRaw(SyntheticPs1Cd.Image());
        // Publication checks require observed live native execution, not a near-empty codec run.
        // Keep a complete generated PCM track; parser fixtures retain their small layouts.
        byte[] audio = new byte[8192 * 2352];
        new Random(0x4344).NextBytes(audio);
        byte[] cue = Encoding.ASCII.GetBytes("FILE \"data.bin\" BINARY\n TRACK 01 MODE1/2352\n INDEX 01 00:00:00\nFILE \"audio.bin\" BINARY\n TRACK 02 AUDIO\n INDEX 01 00:00:00\n");
        db.Platforms.Add(new()
        {
            Id = 1,
            Name = "PlayStation",
            Ownership = ReferenceOwnership.Romd,
            BuiltInVersion = 1,
            BaseName = "PlayStation",
            BaseCompactLabel = "PS",
            ShortName = saturn ? "saturn" : "psx",
            CanonicalKey = saturn ? "saturn" : "psx"
        });
        db.Titles.Add(new() { Id = 1, PlatformId = 1, Name = "Generated CD", NormalizedName = "generated cd", EnrichmentStatus = "Completed" });
        db.CatalogReleases.Add(new() { Id = 1, PlatformId = 1, CatalogTitleId = 1, Fingerprint = "generated-cd", Name = "Generated CD" });
        await db.SaveChangesAsync();
        async Task<RomRepresentationFacts> Original(byte[] bytes, string name)
        {
            var identity = MediaFactFixtures.Identity(bytes);
            await using var temp = await media.Staging.CreateAsync(new MemoryStream(bytes));
            await using var staged = await media.Files.StageAsync(temp);
            await using var transaction = await work.BeginTransactionAsync();
            var file = await media.Files.PublishAndFlushAsync(db, staged);
            var facts = new MediaFactPublisher(db);
            int rom = await facts.CreateLogicalAsync(name, identity, Guid.Empty, DateTimeOffset.UnixEpoch);
            var measured = MediaFactFixtures.WholeMeasurement(Guid.NewGuid(), identity.Sha256, identity.Size, identity,
                RomContainerFacts.Identity.Instance, new RomDecoderIdentity.Managed("import-hasher/v1"), "complete-import/v1",
                "generated-input", DateTimeOffset.UnixEpoch, null, null);
            var graph = await facts.PublishAsync(file.File.Id, RomContainerFacts.Identity.Instance, RomReconstructionManifest.WholeFile.Instance,
                measured, [new(RomOutputSelector.Whole.Instance, rom, true)], []);
            db.CatalogReleaseFiles.Add(new()
            {
                CatalogReleaseId = 1,
                FileFingerprint = identity.Sha1.ToString(),
                Name = name,
                Size = identity.Size,
                Sha1 = identity.Sha1,
                Md5 = identity.Md5,
                Crc = identity.Crc32
            });
            await work.FlushAsync();
            await transaction.CommitAsync();
            return graph;
        }
        var first = await Original(data, "data.bin");
        var second = await Original(audio, "audio.bin");
        int layoutId;
        await using (var temp = await media.Staging.CreateAsync(new MemoryStream(cue)))
        await using (var staged = await media.Files.StageAsync(temp))
        await using (var transaction = await work.BeginTransactionAsync())
        {
            var file = await media.Files.PublishAndFlushAsync(db, staged);
            await new OriginalDiscLayoutService(db, new FileMutationLock(db)).RecordStagedAsync(Guid.NewGuid(), "disc.cue", "disc.cue",
                file.File.Id, OriginalCueDescriptor.Parse(cue, "disc.cue"), default);
            await work.FlushAsync();
            layoutId = await db.OriginalDiscLayouts.Select(row => row.Id).SingleAsync();
            await transaction.CommitAsync();
        }
        var pending = await db.OriginalDiscLayouts.SingleAsync();
        var confirmed = await new DecideOriginalDiscLayoutCommandHandler(new OriginalDiscLayoutService(db, new FileMutationLock(db)),
            work, MediaTestServices.CreateReconciliationRequests(db, TimeProvider.System), Microsoft.Extensions.Logging.Abstractions.NullLogger<DecideOriginalDiscLayoutCommandHandler>.Instance).HandleAsync(new(layoutId,
                new("confirm", IdCoder.Encode(1), [new("data.bin", IdCoder.Encode(first.Outputs.Single().RomId!.Value)),
                    new("audio.bin", IdCoder.Encode(second.Outputs.Single().RomId!.Value))], "Generated native publication regression"),
                RevisionPrecondition.For(pending.Revision.ToString()), Guid.NewGuid()), default);
        confirmed.IsError.ShouldBeFalse(confirmed.IsError ? confirmed.FirstError.ToString() : null);
        Guid job;
        await using (var transaction = await work.BeginTransactionAsync())
        {
            var requested = await new MediaReconciliationRequests(db, TimeProvider.System).StageSubjectAsync(new(Guid.NewGuid(),
                new MediaReconciliationSubject.OriginalLayout(layoutId), true, MediaReconciliationCause.Admin, Guid.NewGuid(), null));
            requested.IsError.ShouldBeFalse();
            job = requested.Value.Job.Id;
            await transaction.CommitAsync();
        }
        var repository = new MediaReconciliationJobRepository(db, TimeProvider.System);
        var claim = (await new ClaimedJobRepository<MediaReconciliationJob, MediaReconciliationJobRepository>(repository, db,
            TimeProvider.System, new()).TryClaimExecutionAsync(job, "generated-native-cd-publication")).ShouldNotBeNull();
        var decision = new MediaReconciliationDecision(media.Composition.Kinds, media.Composition.Reconciliation, media.Capabilities, media.Profile);
        var planning = new MediaReconciliationPlanning(new MediaReconciliationSnapshotReader(database.CreateOptions(), media.Composition.Kinds,
            new RomCdDescriptorIdentityReader(media.Files)), decision);
        var execution = new MediaReconciliationExecution(db, database.CreateOptions(), work, media.Runtime, TimeProvider.System, media.Composition.Kinds, decision);
        var publisher = new MediaReconciliationPublication(media.Files, work, execution, execution, media.Runtime);
        string artifactPath;
        using (JobExecutionFenceScope.Enter(job, claim.FenceToken, TimeProvider.System))
        {
            var wanted = (await execution.ReadDemandAsync(job)).ShouldNotBeNull();
            var encode = (await planning.NextAsync(job, wanted)).Value.ShouldBeOfType<MediaReconciliationDirective.Encode>();
            var captured = await execution.CaptureAsync(encode.Capture);
            captured.IsError.ShouldBeFalse();
            var admission = await media.Encoding.AcquireAsync(captured.Value);
            admission.IsError.ShouldBeFalse();
            await using (var lease = admission.Value)
            {
                var result = await lease.EncodeAndVerifyAsync(_ => Task.CompletedTask);
                output.WriteLine(media.ChildTrace);
                result.IsError.ShouldBeFalse(result.IsError ? result.FirstError + "\n" + media.ChildTrace : null);
                var encoded = result.Value.ShouldBeOfType<MediaReconciliationEncoded.Cd>();
                encoded.Recipe.Value.ShouldBe(saturn ? "chd-saturn-canonical/v1" : "chd-ps1-canonical/v1");
                if (saturn)
                    encoded.Observation.Boot.ShouldBeOfType<RomSaturnCdBoot>();
                else
                    encoded.Observation.Boot.ShouldBeOfType<RomPs1CdBoot>();
                encoded.Artifact.DatHashes.ShouldBeNull();
                artifactPath = encoded.Artifact.Path;
                byte[] actual = await File.ReadAllBytesAsync(artifactPath);
                var expectedMd5 = Md5.FromBytes(MD5.HashData(actual));
                var expectedSha1 = Sha1.FromBytes(SHA1.HashData(actual));
                encoded.ArtifactMeasurement.Md5.ShouldBe(expectedMd5);
                encoded.ArtifactMeasurement.Sha1.ShouldBe(expectedSha1);
                lease.ProcessTreeStopped.ShouldBeTrue();
                (await lease.ReleaseProcessingAsync()).IsError.ShouldBeFalse();
                if (publication != "commit")
                {
                    // An independently retained CAS object already owns the correct artifact ledger.
                    // The conflicting publication must not overwrite it or advance media facts.
                    await using (var staged = await media.Files.StageRawAsync(encoded.Artifact))
                    await using (var transaction = await work.BeginTransactionAsync())
                    {
                        var existing = await media.Files.PublishAndFlushAsync(db, staged);
                        await media.Files.RecordArtifactChecksumsAsync(existing.File.Id, encoded.ArtifactMeasurement);
                        await transaction.CommitAsync();
                    }
                    var invalid = encoded.ArtifactMeasurement with
                    {
                        Md5 = publication == "wrong-md5" ? Md5.FromBytes(MD5.HashData("other"u8)) : expectedMd5,
                        Sha1 = publication == "wrong-sha1" ? Sha1.FromBytes(SHA1.HashData("other"u8)) : expectedSha1,
                    };
                    var conflicting = new MediaReconciliationEncoded.Cd(encoded.Artifact, invalid, encoded.Measured, encoded.Observation,
                        encoded.StoredOriginals, encoded.ComparedAt);
                    await Should.ThrowAsync<InvalidDataException>(() => publisher.PublishAsync(captured.Value, conflicting, true));
                    (await execution.ReadAttemptOutcomeAsync(job, captured.Value.Number)).ShouldBeNull();
                    (await db.Set<RomVerificationEntity>().AnyAsync(row => row.Id == encoded.Measured.Measurement.Id)).ShouldBeFalse();
                    var unchanged = await db.Files.SingleAsync(file => file.Sha256 == encoded.Artifact.FileSha256);
                    unchanged.ArtifactMd5.ShouldBe(expectedMd5);
                    unchanged.ArtifactSha1.ShouldBe(expectedSha1);
                    (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(3);
                    (await db.RomFiles.Where(row => row.Id == first.Outputs.Single().RomId).Select(row => row.PreferredOutputId).SingleAsync())
                        .ShouldBe(first.Outputs.Single().Id);
                }
                (await publisher.PublishAsync(captured.Value, encoded, true)).IsError.ShouldBeFalse();
                var outcome = (await execution.ReadAttemptOutcomeAsync(job, captured.Value.Number)).ShouldNotBeNull();
                outcome.Disposition.ShouldBe(MediaReconciliationAttemptDisposition.Recorded);
                var retained = await db.Files.SingleAsync(file => file.Sha256 == encoded.Artifact.FileSha256);
                retained.ArtifactMd5.ShouldBe(expectedMd5);
                retained.ArtifactSha1.ShouldBe(expectedSha1);
                retained.Size.ShouldBe(actual.LongLength);
                retained.IsCompressed.ShouldBeFalse();
                (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(0);
                (await db.Set<RomVerificationEntity>().AnyAsync(row => row.Id == encoded.Measured.Measurement.Id)).ShouldBeTrue();
                (await db.RomFiles.CountAsync()).ShouldBe(2);
                int representationId = await db.Set<RomVerificationEntity>().Where(row => row.Id == encoded.Measured.Measurement.Id).Select(row => row.RepresentationId).SingleAsync();
                var graph = (await new MediaFactReader(db).ReadAsync([representationId]))[representationId];
                graph.CdBootInspections.Single().Boot.ShouldBe(encoded.Observation.Boot);
                graph.Creations.Single().Recipe.ShouldBe(encoded.Recipe);
                foreach (var (original, expected) in new[] { (first, data), (second, audio) })
                {
                    await using var decoded = (await media.Reader.OpenVerifiedByRomIdAsync(original.Outputs.Single().RomId!.Value)).ShouldNotBeNull();
                    using var copy = new MemoryStream();
                    await decoded.CopyToAsync(copy);
                    copy.ToArray().ShouldBe(expected);
                }
                File.Exists(artifactPath).ShouldBeTrue();
            }
        }
        File.Exists(artifactPath).ShouldBeFalse();
        (await new FileRepository(db).GetUnreferencedFileIdsOlderThanAsync(DateTimeOffset.MaxValue, default)).ShouldBeEmpty();
    }
}
