using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Source.Rom;
using Romd.Domain.Hashing;
using Romd.Media.TestSupport;
using Romd.Persistence.Repositories;
using Romd.Infrastructure.Storage;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.MediaFacts;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class MediaReconciliationSchemaTests
{
    // Synthetic complete facts isolate PostgreSQL publication from independently exercised native codecs and CAS staging.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Publication_Cd_ProofCreationOwnershipJournalAndAllPinsCommitOrRollbackTogether(bool reuseCurrentProof, bool commit)
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        await LayoutSourcesAsync(db, graph);
        var recipe = RomMediaPolicyComposition.Create().CdRecipe!;
        var old = (RomContainerFacts.Chd)graph.Cd.Container;
        var container = MediaFactFixtures.Chd(old.LogicalBytes, recipe.HunkBytes, 2448, recipe.Codecs, old.StoredRawSha1, old.StoredOverallSha1, old.Metadata);
        db.Files.Add(new() { Id = 4, Sha256 = MediaFactGraphFixture.Hash('e'), Size = 512, SizeOnDisk = 512 });
        await db.SaveChangesAsync();
        RomRepresentationFacts target;
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var original = graph.Cd.Verifications.Single();
            var proof = new RomRepresentationVerification(Guid.NewGuid(), MediaFactGraphFixture.Hash('e'), 512,
                reuseCurrentProof ? new RomDecoderIdentity.Native(TargetDecoder) : original.Decoder, Romd.Media.PlayStation.ChdCdVerification.CurrentProfile, "fixture-verifier", DateTimeOffset.UnixEpoch,
                original.Outputs, container, container.StoredRawSha1, container.StoredOverallSha1);
            target = await new MediaFactPublisher(db).PublishAsync(4, container, graph.Cd.Manifest, proof,
                [new(new RomOutputSelector.Member(0), graph.RomId, false)], graph.Cd.Descriptors);
            if (reuseCurrentProof)
            {
                var priorDecoder = new RomRepresentationVerification(Guid.NewGuid(), proof.ArtifactSha256, proof.ArtifactSize, original.Decoder, proof.CheckedProfile,
                    proof.VerifierBuild, proof.ObservedAt.AddTicks(1), proof.Outputs, container, container.StoredRawSha1, container.StoredOverallSha1);
                (await new MediaFactPublisher(db).AppendVerificationAsync(target.Id, priorDecoder)).ShouldBeTrue();
            }
            await transaction.CommitAsync();
        }
        if (reuseCurrentProof)
            await db.OriginalDiscLayoutFiles.ExecuteUpdateAsync(set => set.SetProperty(row => row.FileId, target.FileId));
        var snapshots = SnapshotReader(graph);
        var before = (await snapshots.ReadAsync(new MediaReconciliationSubject.OriginalLayout(1))).Value.ShouldBeOfType<MediaReconciliationFacts.Layout>();
        var decision = PolicyDecision(TargetDecoder, true);
        var claim = await AcceptExecutionAsync(db, new MediaReconciliationSubject.OriginalLayout(1), true);
        var execution = Execution(db, StoppedCustody(), decision);
        var demand = (await execution.ReadDemandAsync(claim.Job.Id)).ShouldNotBeNull();
        var directive = decision.Decide(claim.Job.Id, demand, before).Value.ShouldBeOfType<MediaReconciliationDirective.Encode>();
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        var captured = await execution.CaptureAsync(directive.Capture);
        captured.IsError.ShouldBeFalse();
        int expectedPins = reuseCurrentProof ? 3 : 2;
        if (reuseCurrentProof)
            captured.Value.Capture.Sources.Inputs.OfType<MediaReconciliationInput.Artifact>().Single().Graph.Id.ShouldBe(graph.Source.Id);
        var input = captured.Value.Capture.Sources.Inputs.OfType<MediaReconciliationInput.Logical>().Single();
        input.Graph.Id.ShouldBe(reuseCurrentProof ? target.Id : graph.Source.Id);
        var actual = new RomRepresentationVerification(Guid.NewGuid(), target.ArtifactSha256, target.ArtifactSize, new RomDecoderIdentity.Native(TargetDecoder),
            Romd.Media.PlayStation.ChdCdVerification.CurrentProfile, "fixture-verifier", DateTimeOffset.UnixEpoch.AddTicks(10), target.Verifications.Single().Outputs, container, container.StoredRawSha1, container.StoredOverallSha1);
        var measured = new RomMeasuredRepresentation(container, target.Manifest, actual);
        var encoded = SyntheticCdEncoding(measured, directive.Capture.Identity.Recipe!, PublicationEncoder, MediaFactGraphFixture.Hash('b'), 128,
            "fixture-inspector", MediaFactGraphFixture.Boot, DateTimeOffset.UnixEpoch.AddTicks(20));
        var observation = encoded.Observation;
        var originalCue = ((RomReconstructionManifest.CdTracks)target.Manifest).Members.OfType<RomCdStoredMember>().Single();
        Guid expectedProof = reuseCurrentProof ? target.Verifications.Single().Id : actual.Id;
        if (!reuseCurrentProof && !commit)
        {
            await using var missingLock = await new EfUnitOfWork(db).BeginTransactionAsync();
            (await execution.PrepareAsync(captured.Value, [])).IsError.ShouldBeFalse();
            (await execution.StageEncodedAsync(captured.Value, encoded, target.FileId, target.Descriptors)).FirstError.ShouldBe(MediaReconciliationErrors.Measurement);
            (await execution.ReadAttemptOutcomeAsync(claim.Job.Id, captured.Value.Number)).ShouldBeNull();
            await missingLock.RollbackAsync();
        }
        await using (var transaction = await new EfUnitOfWork(db).BeginTransactionAsync())
        {
            (await execution.PrepareAsync(captured.Value, [target.ArtifactSha256, originalCue.Identity.Sha256])).IsError.ShouldBeFalse();
            (await execution.StageEncodedAsync(captured.Value, encoded, target.FileId, target.Descriptors)).IsError.ShouldBeFalse();
            await using var outside = _database.CreateContext();
            (await outside.CdArchiveOwners.CountAsync()).ShouldBe(0);
            (await outside.RomFiles.SingleAsync()).PreferredOutputId.ShouldBe(graph.Source.Outputs.Single().Id);
            (await outside.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(expectedPins);
            (await execution.ReadAttemptOutcomeAsync(claim.Job.Id, captured.Value.Number)).ShouldBeNull();
            if (commit)
                await transaction.CommitAsync();
            else
                await transaction.RollbackAsync();
        }
        await using var reader = _database.CreateContext();
        var retained = (await new MediaFactReader(reader).ReadAsync([target.Id]))[target.Id];
        retained.Verifications.Count.ShouldBe(reuseCurrentProof || commit ? 2 : 1);
        retained.Creations.Count.ShouldBe(commit ? 1 : 0);
        (await reader.CdArchiveOwners.CountAsync()).ShouldBe(commit ? 1 : 0);
        (await reader.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(commit ? 0 : expectedPins);
        (await reader.RomFiles.SingleAsync()).PreferredOutputId.ShouldBe(commit ? target.Outputs.Single().Id : graph.Source.Outputs.Single().Id);
        (await reader.OriginalDiscLayoutFiles.SingleAsync()).FileId.ShouldBe(commit || reuseCurrentProof ? target.FileId : graph.Source.FileId);
        var journal = await execution.ReadAttemptOutcomeAsync(claim.Job.Id, captured.Value.Number);
        if (commit)
        {
            journal.ShouldNotBeNull().Disposition.ShouldBe(MediaReconciliationAttemptDisposition.Recorded);
            journal.VerificationId.ShouldBe(expectedProof);
            journal.CreationId.ShouldBe(observation.Id);
            var creation = retained.Creations.Single();
            creation.VerificationId.ShouldBe(expectedProof);
            creation.ComparedAt.ShouldBe(encoded.ComparedAt);
            creation.Sources.Single().VerificationId.ShouldBe(input.VerificationId);
            creation.Sources.Single().RepresentationId.ShouldBe(input.Graph.Id);
            retained.Verifications.ShouldContain(proof => proof.Decoder.Equals(graph.Cd.Verifications.Single().Decoder));
            if (reuseCurrentProof)
                creation.Sources.Single().VerificationId.ShouldBe(creation.VerificationId);
            (await execution.ReadOutcomeAsync(claim.Job.Id)).ShouldBeNull(); // publication does not acknowledge a newer demand or terminate the job
            if (!reuseCurrentProof)
            {
                // A later import can retain redundant original bytes while this subject is still active.
                // The next decision releases them through the real publisher, retaining the same proof and creation.
                await using (var transaction = await new EfUnitOfWork(db).BeginTransactionAsync())
                {
                    int originalOutputId = graph.Source.Outputs.Single().Id;
                    await db.RepresentationOutputs.Where(row => row.Id == originalOutputId).ExecuteUpdateAsync(set => set.SetProperty(row => row.RomId, graph.RomId));
                    await transaction.CommitAsync();
                }
                var releaseFacts = (await snapshots.ReadAsync(new MediaReconciliationSubject.OriginalLayout(1))).Value;
                var releaseDemand = (await execution.ReadDemandAsync(claim.Job.Id)).ShouldNotBeNull();
                var release = decision.Decide(claim.Job.Id, releaseDemand, releaseFacts).Value.ShouldBeOfType<MediaReconciliationDirective.Release>();
                var releaseAttempt = await execution.CaptureAsync(release.Capture);
                releaseAttempt.IsError.ShouldBeFalse();
                var unusedFiles = Substitute.For<IFileStorageService>();
                var publisher = new MediaReconciliationPublication(unusedFiles, new EfUnitOfWork(db), execution, execution, StoppedCustody());
                (await publisher.ReleaseAsync(releaseAttempt.Value, release.RepresentationId, release.ProofId, true)).IsError.ShouldBeFalse();
                unusedFiles.ReceivedCalls().ShouldBeEmpty();
                var released = (await new MediaFactReader(db).ReadAsync([target.Id]))[target.Id];
                released.Verifications.Count.ShouldBe(2);
                released.Creations.ShouldHaveSingleItem().Id.ShouldBe(creation.Id);
                int originalOutput = graph.Source.Outputs.Single().Id;
                (await db.RepresentationOutputs.SingleAsync(row => row.Id == originalOutput)).RomId.ShouldBeNull();
                (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(0);
                (await execution.ReadAttemptOutcomeAsync(claim.Job.Id, releaseAttempt.Value.Number)).ShouldNotBeNull().Code.ShouldBe("released");
                (await publisher.ReleaseAsync(releaseAttempt.Value, -1, Guid.Empty, false)).IsError.ShouldBeFalse();
            }
        }
        else
            journal.ShouldBeNull();
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Publication_Whole_RetainsObservedBuildsAndRawFactsWithAtomicSourceReleaseAndCurrentDemand(bool commit, bool wrongParameters)
    {
        await using var db = _database.CreateContext();
        byte[] raw = SyntheticNintendoIso.GameCube(), encodedBytes = Enumerable.Range(0, 512).Select(index => (byte)(index * 7)).ToArray();
        var identity = MediaFactFixtures.Identity(raw);
        var physical = MediaFactFixtures.Identity(encodedBytes);
        db.Files.AddRange(new FileEntityPersistence { Id = 1, Sha256 = identity.Sha256, Size = identity.Size, SizeOnDisk = identity.Size },
            new FileEntityPersistence { Id = 2, Sha256 = physical.Sha256, Size = physical.Size, SizeOnDisk = physical.Size });
        await db.SaveChangesAsync();
        RomRepresentationFacts source;
        Guid sourceInspection;
        var inspector = new RomDecoderIdentity.Managed("fixture-inspector");
        var originalProof = MediaFactFixtures.WholeMeasurement(Guid.NewGuid(), identity.Sha256, identity.Size, identity, RomContainerFacts.Identity.Instance,
            new RomDecoderIdentity.Managed("fixture-hasher"), "identity/v1", "fixture-verifier", DateTimeOffset.UnixEpoch, null, null);
        var currentSourceProof = MediaFactFixtures.WholeMeasurement(Guid.NewGuid(), identity.Sha256, identity.Size, identity, RomContainerFacts.Identity.Instance,
            inspector, "complete-original/v1", "fixture-verifier", DateTimeOffset.UnixEpoch.AddTicks(10), null, null);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var publisher = new MediaFactPublisher(db);
            int rom = await publisher.CreateLogicalAsync("disc.iso", identity, Guid.Empty, DateTimeOffset.UnixEpoch);
            source = await publisher.PublishAsync(1, RomContainerFacts.Identity.Instance, RomReconstructionManifest.WholeFile.Instance, originalProof, [new(RomOutputSelector.Whole.Instance, rom, true)], []);
            (await publisher.AppendVerificationAsync(source.Id, currentSourceProof)).ShouldBeTrue();
            var observed = new RomLogicalInspection(Guid.NewGuid(), identity, "logical/v1", "fixture-source-inspector", currentSourceProof.ObservedAt, raw.AsSpan(0, 32), new RomNintendoLayoutFacts(0, 0, 0));
            var inspection = await new LogicalInspectionFactWriter(db).StageAsync(rom, new(source.Id, source.Outputs.Single().Id, 1, currentSourceProof.Id), observed, default);
            inspection.IsError.ShouldBeFalse();
            sourceInspection = inspection.Value.Id;
            await transaction.CommitAsync();
        }
        int romId = source.Outputs.Single().RomId!.Value;
        var claim = await AcceptExecutionAsync(db, new MediaReconciliationSubject.LogicalRom(romId), false);
        var decision = PolicyDecision(TargetDecoder, true);
        var execution = Execution(db, StoppedCustody(), decision);
        var unusedDescriptors = Substitute.For<Romd.Admin.Application.Source.Rom.IRomCdDescriptorIdentityReader>();
        var snapshots = new Romd.Persistence.Repositories.Reconciliation.MediaReconciliationSnapshotReader(_database.CreateOptions(), RomMediaPolicyComposition.Create().Kinds, unusedDescriptors);
        var facts = (await snapshots.ReadAsync(new MediaReconciliationSubject.LogicalRom(romId))).Value;
        var demand = (await execution.ReadDemandAsync(claim.Job.Id)).ShouldNotBeNull();
        var directive = decision.Decide(claim.Job.Id, demand, facts).Value.ShouldBeOfType<MediaReconciliationDirective.Encode>();
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        var captured = await execution.CaptureAsync(directive.Capture);
        captured.IsError.ShouldBeFalse();
        captured.Value.Capture.Sources.LogicalInspectionIds.ShouldBe([sourceInspection]);
        captured.Value.Capture.Sources.Inputs.OfType<MediaReconciliationInput.Logical>().Single().VerificationId.ShouldBe(originalProof.Id);
        var container = MediaFactFixtures.Rvz(identity.Size, 1, 5, wrongParameters ? 18 : 19, 131072, false);
        var targetProof = MediaFactFixtures.WholeMeasurement(Guid.NewGuid(), physical.Sha256, physical.Size, identity, container,
            directive.Capture.Decoder, "complete-rvz/v1", "fixture-verifier", DateTimeOffset.UnixEpoch.AddTicks(20), null, null);
        var sourceObservation = new RomLogicalInspection(Guid.NewGuid(), identity, "logical/v1", "fixture-source-inspector", DateTimeOffset.UnixEpoch.AddTicks(20), raw.AsSpan(0, 32), new RomNintendoLayoutFacts(0, 0, 0));
        var targetObservation = new RomLogicalInspection(Guid.NewGuid(), identity, "logical/v1", "fixture-target-inspector", DateTimeOffset.UnixEpoch.AddTicks(20), raw.AsSpan(0, 32), new RomNintendoLayoutFacts(0, 0, 0));
        var sourceMeasured = new RomMediaVerificationMeasurement.Whole(new(RomContainerFacts.Identity.Instance, source.Manifest, currentSourceProof), sourceObservation,
            new(identity.Sha256, identity.Md5, identity.Sha1, identity.Size));
        var targetMeasured = new RomMediaVerificationMeasurement.Whole(new(container, source.Manifest, targetProof), targetObservation, new(physical.Sha256, physical.Md5, physical.Sha1, physical.Size));
        var artifact = Substitute.For<ITempFile>();
        artifact.FileSha256.Returns(physical.Sha256);
        artifact.FileSize.Returns(physical.Size);
        var encoded = new MediaReconciliationEncoded.Whole(artifact, sourceMeasured, targetMeasured, directive.Capture.Identity.Recipe!, directive.Capture.EncoderBuild!, null, DateTimeOffset.UnixEpoch.AddTicks(30));
        await using (var transaction = await new EfUnitOfWork(db).BeginTransactionAsync())
        {
            (await execution.PrepareAsync(captured.Value, [physical.Sha256])).IsError.ShouldBeFalse();
            var staged = await execution.StageEncodedAsync(captured.Value, encoded, 2, []);
            if (wrongParameters)
                staged.FirstError.ShouldBe(MediaReconciliationErrors.Measurement);
            else
                staged.IsError.ShouldBeFalse();
            await using var outside = _database.CreateContext();
            (await outside.Database.SqlQuery<int>($"""SELECT count(*)::integer AS "Value" FROM romd."LogicalInspections" """).SingleAsync()).ShouldBe(1);
            (await outside.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(1);
            (await outside.RomFiles.SingleAsync()).PreferredOutputId.ShouldBe(source.Outputs.Single().Id);
            if (commit)
                await transaction.CommitAsync();
            else
                await transaction.RollbackAsync();
        }
        await using var reader = _database.CreateContext();
        var sourceRetained = (await new MediaFactReader(reader).ReadAsync([source.Id]))[source.Id];
        sourceRetained.Verifications.Select(row => row.Id).ShouldBe([originalProof.Id, currentSourceProof.Id], ignoreOrder: true);
        sourceRetained.Outputs.Single().RomId.ShouldBe(commit ? null : romId);
        (await reader.Database.SqlQuery<int>($"""SELECT count(*)::integer AS "Value" FROM romd."LogicalInspections" """).SingleAsync()).ShouldBe(commit ? 2 : 1);
        (await reader.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(commit ? 0 : 1);
        var wanted = (await execution.ReadDemandAsync(claim.Job.Id)).ShouldNotBeNull();
        wanted.Version.ShouldBe(commit ? 2 : 1);
        (await execution.ReadOutcomeAsync(claim.Job.Id)).ShouldBeNull();
        var journal = await execution.ReadAttemptOutcomeAsync(claim.Job.Id, captured.Value.Number);
        if (commit)
        {
            journal.ShouldNotBeNull().VerificationId.ShouldBe(targetProof.Id);
            var target = (await new MediaFactReader(reader).ReadAsync([journal.RepresentationId!.Value]))[journal.RepresentationId.Value];
            target.Creations.Single().Sources.Single().VerificationId.ShouldBe(originalProof.Id);
            target.Creations.Single().ComparedAt.ShouldBe(encoded.ComparedAt);
            (await reader.RomFiles.SingleAsync()).PreferredOutputId.ShouldBe(target.Outputs.Single().Id);
        }
        else
            journal.ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publication_ReplacedOwnArchive_TransfersItsBackingAndRetainsEveryBuildNeededByAForeignLayout(bool foreignDependency)
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var layout = await LayoutSourcesAsync(db, graph);
        db.Files.Add(new() { Id = 4, Sha256 = MediaFactGraphFixture.Hash('e'), Size = 512, SizeOnDisk = 512 });
        await db.SaveChangesAsync();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var current = new RomRepresentationVerification(Guid.NewGuid(), graph.Cd.ArtifactSha256, graph.Cd.ArtifactSize, new RomDecoderIdentity.Native(TargetDecoder),
                Romd.Media.PlayStation.ChdCdVerification.CurrentProfile, "fixture-verifier", DateTimeOffset.UnixEpoch.AddTicks(1), graph.Cd.Verifications.Single().Outputs, graph.Cd.Container,
                ((RomContainerFacts.Chd)graph.Cd.Container).StoredRawSha1, ((RomContainerFacts.Chd)graph.Cd.Container).StoredOverallSha1);
            (await new MediaFactPublisher(db).AppendVerificationAsync(graph.Cd.Id, current)).ShouldBeTrue();
            var writer = new MediaCreationFactWriter(db);
            Guid boot = await writer.StageCdBootAsync(graph.Cd.Id, graph.Cd.Verifications.Single().Id, "fixture-inspector", DateTimeOffset.UnixEpoch, MediaFactGraphFixture.Boot, default);
            var creation = graph.Creation(boot);
            await writer.StageCreationAsync(graph.Cd.Id, creation, default);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO romd."CdArchiveOwners" ("LayoutId","RepresentationId","VerificationId","PublicationId","LayoutRevision","PlatformId","ReleaseId","CatalogFingerprint","EntryPoint","PublishedAtUtcTicks")
                VALUES (1,{graph.Cd.Id},{graph.Cd.Verifications.Single().Id},{Guid.NewGuid()},{layout.Layout!.Revision},1,1,'fixture-layout','disc.cue',{DateTimeOffset.UtcNow.UtcTicks})
                """);
            await db.OriginalDiscLayoutFiles.ExecuteUpdateAsync(set => set.SetProperty(row => row.FileId, graph.Cd.FileId));
            await transaction.CommitAsync();
        }
        if (foreignDependency)
        {
            db.OriginalDiscLayouts.Add(new()
            {
                Id = 2,
                Revision = Guid.NewGuid(),
                SourceJobId = Guid.NewGuid(),
                SourceRelativePath = "other/disc.cue",
                EntryPoint = "other/disc.cue",
                DescriptorFileId = 3,
                DependencyPathsJson = "[\"other/disc.bin\"]",
                Files = [new() { Path = "other/disc.bin", RomId = graph.RomId, FileId = graph.Cd.FileId }]
            });
            await db.SaveChangesAsync();
        }
        var decision = PolicyDecision(TargetDecoder, true);
        var execution = Execution(db, StoppedCustody(), decision);
        var claim = await AcceptExecutionAsync(db, new MediaReconciliationSubject.OriginalLayout(1), true);
        var facts = (await SnapshotReader(graph).ReadAsync(new MediaReconciliationSubject.OriginalLayout(1))).Value.ShouldBeOfType<MediaReconciliationFacts.Layout>();
        facts.Sources.Inputs.OfType<MediaReconciliationInput.Artifact>().Single().Graph.Id.ShouldBe(graph.Source.Id);
        var demand = (await execution.ReadDemandAsync(claim.Job.Id)).ShouldNotBeNull();
        var directive = decision.Decide(claim.Job.Id, demand, facts).Value.ShouldBeOfType<MediaReconciliationDirective.Encode>();
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        var capture = await execution.CaptureAsync(directive.Capture);
        capture.IsError.ShouldBeFalse();
        var recipe = RomMediaPolicyComposition.Create().CdRecipe!;
        var old = (RomContainerFacts.Chd)graph.Cd.Container;
        var container = MediaFactFixtures.Chd(old.LogicalBytes, recipe.HunkBytes, 2448, recipe.Codecs, old.StoredRawSha1, old.StoredOverallSha1, old.Metadata);
        var proof = new RomRepresentationVerification(Guid.NewGuid(), MediaFactGraphFixture.Hash('e'), 512, new RomDecoderIdentity.Native(TargetDecoder), Romd.Media.PlayStation.ChdCdVerification.CurrentProfile,
            "fixture-verifier", DateTimeOffset.UnixEpoch.AddTicks(30), graph.Cd.Verifications.Single().Outputs, container, container.StoredRawSha1, container.StoredOverallSha1);
        var encoded = SyntheticCdEncoding(new(container, graph.Cd.Manifest, proof), directive.Capture.Identity.Recipe!, PublicationEncoder,
            MediaFactGraphFixture.Hash('b'), 128, "fixture-inspector", MediaFactGraphFixture.Boot, DateTimeOffset.UnixEpoch.AddTicks(40));
        await using (var transaction = await new EfUnitOfWork(db).BeginTransactionAsync())
        {
            (await execution.PrepareAsync(capture.Value, [proof.ArtifactSha256, graph.Cd.Descriptors.Single().Sha256])).IsError.ShouldBeFalse();
            (await execution.StageEncodedAsync(capture.Value, encoded, 4, graph.Cd.Descriptors)).IsError.ShouldBeFalse();
            await transaction.CommitAsync();
        }
        var journal = (await execution.ReadAttemptOutcomeAsync(claim.Job.Id, capture.Value.Number)).ShouldNotBeNull();
        var target = (await new MediaFactReader(db).ReadAsync([journal.RepresentationId!.Value]))[journal.RepresentationId.Value];
        (await db.OriginalDiscLayoutFiles.SingleAsync(row => row.OriginalDiscLayoutId == 1)).FileId.ShouldBe(4);
        (await db.CdArchiveOwners.SingleAsync()).RepresentationId.ShouldBe(target.Id);
        int rawOutput = graph.Source.Outputs.Single().Id;
        (await db.RepresentationOutputs.SingleAsync(row => row.Id == rawOutput)).RomId.ShouldBeNull();
        var oldGraph = (await new MediaFactReader(db).ReadAsync([graph.Cd.Id]))[graph.Cd.Id];
        oldGraph.Verifications.Count.ShouldBe(2);
        oldGraph.Outputs.Single().RomId.ShouldBe(foreignDependency ? graph.RomId : null);
        if (foreignDependency)
            (await db.OriginalDiscLayoutFiles.SingleAsync(row => row.OriginalDiscLayoutId == 2)).FileId.ShouldBe(graph.Cd.FileId);
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(0);
        (await new MediaRepresentationCleanup(db).CollectAsync(256)).ShouldBe(foreignDependency ? 1 : 2);
        var remaining = await new MediaFactReader(db).ReadAsync([graph.Cd.Id, target.Id]);
        if (foreignDependency)
            remaining[graph.Cd.Id].Verifications.Count.ShouldBe(2);
        else
            remaining.ContainsKey(graph.Cd.Id).ShouldBeFalse();
        remaining[target.Id].Creations.Single().Sources.Single().RepresentationId.ShouldBe(graph.Cd.Id);
    }
    private static MediaReconciliationEncoded.Cd SyntheticCdEncoding(RomMeasuredRepresentation measured, RecipeId recipe, RomChdEncoderBuild encoder,
        Sha256 requestHash, long requestBytes, string inspector, RomPs1CdBoot boot, DateTimeOffset comparedAt)
    {
        var artifact = Substitute.For<ITempFile>();
        artifact.FileSha256.Returns(measured.Measurement.ArtifactSha256);
        artifact.FileSize.Returns(measured.Measurement.ArtifactSize);
        var originals = ((RomReconstructionManifest.CdTracks)measured.Manifest).Members.OfType<RomCdStoredMember>().ToDictionary(member => member.Path, member =>
        {
            var file = Substitute.For<ITempFile>();
            file.FileSha256.Returns(member.Identity.Sha256);
            file.FileSize.Returns(member.Identity.Size);
            return file;
        }, StringComparer.Ordinal);
        return new(artifact, new(artifact.FileSha256, Md5.Parse(new string('e', 32)), Sha1.Parse(new string('e', 40)), artifact.FileSize), measured, new(measured, recipe, encoder, requestHash, requestBytes, inspector, boot), originals, comparedAt);
    }

}
