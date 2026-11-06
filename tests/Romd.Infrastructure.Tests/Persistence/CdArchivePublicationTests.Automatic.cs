using Romd.Domain.Hashing;
using Romd.Infrastructure.Source;
using Romd.Persistence.Catalog;
using Romd.PostgreSql.TestSupport;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Admin.Application.Storage.Files;
using Romd.Application.Common.Content;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Persistence.Repositories.Reconciliation;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class CdArchivePublicationTests
{
    [Fact]
    public async Task Automatic_ConcurrentLayoutsSharingBin_CaptureIndependentPinsAndCancellationReleasesOnlyItsOwner()
    {
        await SeedSecondReleaseAsync();
        var media = RomMediaPolicyComposition.Create(RomMediaPolicyComposition.Registrations.Select(row =>
            row.ManifestType == MediaManifestType.CdTracks
                ? row with { ConversionMode = ConversionMode.Automatic } : row).ToArray());
        await using var firstDb = database.CreateContext();
        await using var secondDb = database.CreateContext();
        var originalPreferences = await firstDb.RomFiles.OrderBy(row => row.Id).Select(row => row.PreferredOutputId).ToArrayAsync();
        var claims = await Task.WhenAll(ClaimAutomaticAsync(firstDb, 1), ClaimAutomaticAsync(secondDb, 2));
        var firstCustody = Substitute.For<IMediaRunCustody>();
        var secondCustody = Substitute.For<IMediaRunCustody>();
        firstCustody.EnsureJobStoppedAsync(claims[0].Job.Id, Arg.Any<CancellationToken>()).Returns(Result.Success);
        secondCustody.EnsureJobStoppedAsync(claims[1].Job.Id, Arg.Any<CancellationToken>()).Returns(Result.Success);
        var first = AutomaticServices(firstDb, media, Decoder, canEncode: true, qualified: true, firstCustody);
        var second = AutomaticServices(secondDb, media, Decoder, canEncode: true, qualified: true, secondCustody);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int entered = 0;
        async Task<MediaReconciliationAttempt> CaptureAsync(JobExecutionClaim<MediaReconciliationJob> claim,
            (MediaReconciliationPlanning Planning, MediaReconciliationExecution Execution) services)
        {
            using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
            var demand = (await services.Execution.ReadDemandAsync(claim.Job.Id)).ShouldNotBeNull();
            demand.ConversionRequested.ShouldBeFalse();
            demand.Version.ShouldBe(1);
            var encode = (await services.Planning.NextAsync(claim.Job.Id, demand)).Value.ShouldBeOfType<MediaReconciliationDirective.Encode>();
            if (Interlocked.Increment(ref entered) == 2)
                ready.SetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var captured = await services.Execution.CaptureAsync(encode.Capture);
            captured.IsError.ShouldBeFalse();
            return captured.Value;
        }

        var attempts = await Task.WhenAll(CaptureAsync(claims[0], first), CaptureAsync(claims[1], second));

        attempts.ShouldAllBe(attempt => attempt.Number == 1);
        await using var observer = database.CreateContext();
        (await observer.Set<MediaReconciliationPinEntity>().Where(row => row.JobId == claims[0].Job.Id).OrderBy(row => row.FileId).Select(row => row.FileId).ToArrayAsync())
            .ShouldBe([1, 2, 3]);
        (await observer.Set<MediaReconciliationPinEntity>().Where(row => row.JobId == claims[1].Job.Id).OrderBy(row => row.FileId).Select(row => row.FileId).ToArrayAsync())
            .ShouldBe([2, 4, 5]);
        (await observer.Set<MediaReconciliationPinEntity>().CountAsync(row => row.FileId == 2)).ShouldBe(2);
        await using (var requester = database.CreateContext())
        await using (var transaction = await requester.Database.BeginTransactionAsync())
        {
            (await new MediaReconciliationRequests(requester, TimeProvider.System).StageCancellationAsync(claims[0].Job.Id)).IsError.ShouldBeFalse();
            await requester.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        (await observer.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(6, "cancellation records intent before stopped settlement");
        using (JobExecutionFenceScope.Enter(claims[0].Job.Id, claims[0].FenceToken, TimeProvider.System))
        {
            (await first.Execution.FinishAsync(claims[0].Job.Id, 1, MediaReconciliationOutcomeKind.Cancelled, "cancelled", false)).FirstError.ShouldBe(MediaReconciliationErrors.Custody);
            (await observer.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(6);
            (await first.Execution.FinishAsync(claims[0].Job.Id, 1, MediaReconciliationOutcomeKind.Cancelled, "cancelled", true)).IsError.ShouldBeFalse();
        }
        (await observer.Set<MediaReconciliationPinEntity>().Where(row => row.JobId == claims[0].Job.Id).CountAsync()).ShouldBe(0);
        (await observer.Set<MediaReconciliationPinEntity>().Where(row => row.JobId == claims[1].Job.Id).OrderBy(row => row.FileId).Select(row => row.FileId).ToArrayAsync())
            .ShouldBe([2, 4, 5]);
        (await second.Execution.ReadDemandAsync(claims[1].Job.Id)).ShouldNotBeNull().CancelRequested.ShouldBeFalse();
        (await first.Execution.ReadAttemptOutcomeAsync(claims[0].Job.Id, 1)).ShouldNotBeNull().Disposition.ShouldBe(MediaReconciliationAttemptDisposition.Cancelled);
        (await second.Execution.ReadAttemptOutcomeAsync(claims[1].Job.Id, 1)).ShouldBeNull();
        await using (var requester = database.CreateContext())
        await using (var transaction = await requester.Database.BeginTransactionAsync())
        {
            (await new MediaReconciliationRequests(requester, TimeProvider.System).StageCancellationAsync(claims[1].Job.Id)).IsError.ShouldBeFalse();
            await requester.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        using (JobExecutionFenceScope.Enter(claims[1].Job.Id, claims[1].FenceToken, TimeProvider.System))
            (await second.Execution.FinishAsync(claims[1].Job.Id, 1, MediaReconciliationOutcomeKind.Cancelled, "cancelled", true)).IsError.ShouldBeFalse();
        (await observer.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(0);
        (await observer.CdArchiveOwners.CountAsync()).ShouldBe(0);
        (await observer.RomFiles.OrderBy(row => row.Id).Select(row => row.PreferredOutputId).ToArrayAsync()).ShouldBe(originalPreferences);
    }

    [Fact]
    public async Task Automatic_ImportDemand_PublishesOneMeasuredArchiveWithoutAnExplicitConversionRequest()
    {
        var media = RomMediaPolicyComposition.Create(RomMediaPolicyComposition.Registrations.Select(row =>
            row.ManifestType == MediaManifestType.CdTracks
                ? row with { ConversionMode = ConversionMode.Automatic } : row).ToArray());
        await using var db = database.CreateContext();
        var claim = await ClaimAutomaticAsync(db, 1);
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        var custody = Substitute.For<IMediaRunCustody>();
        custody.EnsureJobStoppedAsync(claim.Job.Id, Arg.Any<CancellationToken>()).Returns(Result.Success);
        var services = AutomaticServices(db, media, Decoder, canEncode: true, qualified: true, custody);
        var demand = (await services.Execution.ReadDemandAsync(claim.Job.Id)).ShouldNotBeNull();
        demand.ConversionRequested.ShouldBeFalse();
        var encode = (await services.Planning.NextAsync(claim.Job.Id, demand)).Value.ShouldBeOfType<MediaReconciliationDirective.Encode>();
        var attempt = (await services.Execution.CaptureAsync(encode.Capture)).Value;
        (await db.Set<MediaReconciliationPinEntity>().OrderBy(row => row.FileId).Select(row => row.FileId).ToArrayAsync()).ShouldBe([1, 2, 3]);

        var graph = await PublishAutomaticAttemptAsync(db, services.Execution, attempt, Measured(), 9, new() { ["disc.cue"] = 3 });

        var recorded = (await services.Execution.ReadAttemptOutcomeAsync(claim.Job.Id, attempt.Number)).ShouldNotBeNull();
        recorded.Disposition.ShouldBe(MediaReconciliationAttemptDisposition.Recorded);
        var owner = await db.CdArchiveOwners.SingleAsync();
        owner.LayoutId.ShouldBe(1);
        owner.RepresentationId.ShouldBe(graph.Id);
        owner.VerificationId.ShouldBe(graph.Verifications.Single().Id);
        owner.PublicationId.ShouldBe(claim.Job.Id);
        graph.Outputs.Where(output => output.RomId is not null).Select(output => output.RomId)
            .OrderBy(id => id).ShouldBe([1, 2]);
        (await services.Execution.FinishAsync(claim.Job.Id, demand.Version, MediaReconciliationOutcomeKind.Completed, "Canonical", true)).IsError.ShouldBeFalse();
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(0);
        (await services.Execution.ReadOutcomeAsync(claim.Job.Id)).ShouldNotBeNull().AcknowledgedVersion.ShouldBe(1);
        var next = (await services.Planning.NextAsync(claim.Job.Id, demand)).Value.ShouldBeOfType<MediaReconciliationDirective.Settle>();
        next.Code.ShouldBe(MediaReconciliationNoWork.Canonical.ToString());
    }

    [Fact]
    public async Task Automatic_LaterRegionSharesAnArchivedBin_UsesItsProofWithoutTakingTheFirstLayoutsOwnership()
    {
        var first = await Publish();
        var upload = UploadJob.Create("later-region", createdAt: DateTimeOffset.UtcNow);
        await SeedSecondReleaseAsync(upload.Id, bound: false);
        var media = RomMediaPolicyComposition.Create(RomMediaPolicyComposition.Registrations.Select(row =>
            row.ManifestType == MediaManifestType.CdTracks
                ? row with { ConversionMode = ConversionMode.Automatic } : row).ToArray());
        await using var db = database.CreateContext();
        db.Set<UploadJobEntity>().Add(UploadJobEntity.FromDomain(upload));
        db.JobItems.AddRange(
            JobItemEntity.FromDomain(JobItem.ForRom(upload.Id, "b-track.bin", secondTrack.Length, RomIngestOutcome.Ingested, 4, [], 1, null, createdAt: DateTimeOffset.UtcNow).WithUploadIdentity()),
            JobItemEntity.FromDomain(JobItem.ForRom(upload.Id, "audio.bin", originals["audio.bin"].Length, RomIngestOutcome.Deduplicated, 2, [], 1, null, createdAt: DateTimeOffset.UtcNow).WithUploadIdentity()));
        await db.SaveChangesAsync();
        var intake = new OriginalDiscDescriptorIntake(new OriginalDiscLayoutService(db, new FileMutationLock(db)),
            Substitute.For<IFileStorageService>(), Substitute.For<IJobItemRepository>(), new EfUnitOfWork(db),
            MediaTestServices.CreateReconciliationRequests(db, TimeProvider.System), clock: TimeProvider.System);
        await intake.ReconcileAsync(upload.Id, default);
        var queued = await db.Set<MediaReconciliationDefinitionEntity>().SingleAsync(row => row.SubjectKind == "OriginalLayout" && row.SubjectId == 2);
        var repository = new MediaReconciliationJobRepository(db, TimeProvider.System);
        var claim = (await new ClaimedJobRepository<MediaReconciliationJob, MediaReconciliationJobRepository>(repository, db, TimeProvider.System, new())
            .TryClaimExecutionAsync(queued.JobId, "later-region-import")).ShouldNotBeNull();
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        var custody = Substitute.For<IMediaRunCustody>();
        custody.EnsureJobStoppedAsync(claim.Job.Id, Arg.Any<CancellationToken>()).Returns(Result.Success);
        var services = AutomaticServices(db, media, Decoder, canEncode: true, qualified: true, custody);
        var demand = (await services.Execution.ReadDemandAsync(claim.Job.Id)).ShouldNotBeNull();
        demand.ConversionRequested.ShouldBeFalse();
        var encode = (await services.Planning.NextAsync(claim.Job.Id, demand)).Value.ShouldBeOfType<MediaReconciliationDirective.Encode>();
        var shared = encode.Capture.Sources.Inputs.OfType<MediaReconciliationInput.Logical>().Single(input => input.Output.RomId == 2);
        shared.Graph.Id.ShouldBe(first.Graph.Id);
        var attempt = (await services.Execution.CaptureAsync(encode.Capture)).Value;
        (await db.Set<MediaReconciliationPinEntity>().Where(row => row.JobId == claim.Job.Id).Select(row => row.FileId).Distinct().OrderBy(id => id).ToArrayAsync())
            .ShouldBe([4, 5, 9]);

        var graph = await PublishAutomaticAttemptAsync(db, services.Execution, attempt, SecondMeasured(), 10, new() { ["b.cue"] = 5 });
        (await services.Execution.FinishAsync(claim.Job.Id, demand.Version, MediaReconciliationOutcomeKind.Completed, "Canonical", true)).IsError.ShouldBeFalse();

        var owners = await db.CdArchiveOwners.OrderBy(row => row.LayoutId).ToArrayAsync();
        owners.Select(row => row.RepresentationId).ShouldBe([first.Graph.Id, graph.Id]);
        owners[0].VerificationId.ShouldBe(first.VerificationId);
        owners[0].PublicationId.ShouldBe(first.PublicationId);
        owners[1].VerificationId.ShouldBe(graph.Verifications.Single().Id);
        int preferredOutput = (await db.RomFiles.SingleAsync(row => row.Id == 2)).PreferredOutputId.ShouldNotBeNull();
        (await db.RepresentationOutputs.SingleAsync(row => row.Id == preferredOutput)).RepresentationId.ShouldBe(first.Graph.Id);
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(0);
        (await ArchiveAsync(db, 1)).Verifications.Single().Id.ShouldBe(first.VerificationId);
    }

    [Fact]
    public async Task Automatic_RestartUnderNewFence_ReusesStoppedCapturedWorkAndRefusesTheOldWorker()
    {
        var media = RomMediaPolicyComposition.Create(RomMediaPolicyComposition.Registrations.Select(row =>
            row.ManifestType == MediaManifestType.CdTracks
                ? row with { ConversionMode = ConversionMode.Automatic } : row).ToArray());
        await using var db = database.CreateContext();
        var claim = await ClaimAutomaticAsync(db, 1);
        var custody = Substitute.For<IMediaRunCustody>();
        custody.EnsureJobStoppedAsync(claim.Job.Id, Arg.Any<CancellationToken>()).Returns(Result.Success);
        var services = AutomaticServices(db, media, Decoder, canEncode: true, qualified: true, custody);
        MediaReconciliationCapture capture;
        using (JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System))
        {
            var demand = (await services.Execution.ReadDemandAsync(claim.Job.Id)).ShouldNotBeNull();
            capture = (await services.Planning.NextAsync(claim.Job.Id, demand)).Value.ShouldBeOfType<MediaReconciliationDirective.Encode>().Capture;
            (await services.Execution.CaptureAsync(capture)).Value.Number.ShouldBe(1);
        }
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE romd.\"Jobs\" SET \"ExecutionLeaseExpiresAtUtc\"=0 WHERE \"Id\"={claim.Job.Id}");
        var repository = new MediaReconciliationJobRepository(db, TimeProvider.System);
        var resumed = (await new ClaimedJobRepository<MediaReconciliationJob, MediaReconciliationJobRepository>(repository, db, TimeProvider.System, new())
            .TryClaimExecutionAsync(claim.Job.Id, "automatic-cd-replacement")).ShouldNotBeNull();
        resumed.FenceToken.ShouldNotBe(claim.FenceToken);
        using var nextFence = JobExecutionFenceScope.Enter(resumed.Job.Id, resumed.FenceToken, TimeProvider.System);
        custody.EnsureJobStoppedAsync(claim.Job.Id, Arg.Any<CancellationToken>()).Returns(MediaReconciliationErrors.Custody);
        (await services.Execution.CaptureAsync(capture)).FirstError.ShouldBe(MediaReconciliationErrors.Custody);
        (await db.Set<MediaReconciliationPinEntity>().OrderBy(row => row.FileId).Select(row => row.FileId).ToArrayAsync()).ShouldBe([1, 2, 3]);
        (await services.Execution.ReadAttemptOutcomeAsync(claim.Job.Id, 1)).ShouldBeNull();
        custody.EnsureJobStoppedAsync(claim.Job.Id, Arg.Any<CancellationToken>()).Returns(Result.Success);
        var resumedAttempt = (await services.Execution.CaptureAsync(capture)).Value;
        resumedAttempt.Number.ShouldBe(1);
        using (JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System))
            await Should.ThrowAsync<JobExecutionOwnershipLostException>(() => services.Execution.FailAttemptAsync(resumedAttempt, "stale-worker", false, true));
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(3);

        var graph = await PublishAutomaticAttemptAsync(db, services.Execution, resumedAttempt, Measured(), 9, new() { ["disc.cue"] = 3 });
        var current = (await services.Execution.ReadDemandAsync(claim.Job.Id)).ShouldNotBeNull();
        current.ConversionRequested.ShouldBeFalse();
        (await services.Execution.FinishAsync(claim.Job.Id, current.Version, MediaReconciliationOutcomeKind.Completed, "Canonical", true)).IsError.ShouldBeFalse();
        (await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM romd.\"MediaReconciliationAttempts\" WHERE \"JobId\"={claim.Job.Id}").SingleAsync()).ShouldBe(1);
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(0);
        (await db.CdArchiveOwners.SingleAsync()).RepresentationId.ShouldBe(graph.Id);
    }

    [Fact]
    public async Task Automatic_DecoderUpgrade_StillChoosesVerifyOnlyAndRetainsTheOldBuildsProof()
    {
        var published = await Publish();
        var media = RomMediaPolicyComposition.Create(RomMediaPolicyComposition.Registrations.Select(row =>
            row.ManifestType == MediaManifestType.CdTracks
                ? row with { ConversionMode = ConversionMode.Automatic } : row).ToArray());
        await using var db = database.CreateContext();
        var claim = await ClaimAutomaticAsync(db, 1);
        var custody = Substitute.For<IMediaRunCustody>();
        custody.EnsureJobStoppedAsync(claim.Job.Id, Arg.Any<CancellationToken>()).Returns(Result.Success);
        var services = AutomaticServices(db, media, Upgraded(Decoder), canEncode: false, qualified: true, custody);
        var demand = (await services.Execution.ReadDemandAsync(claim.Job.Id)).ShouldNotBeNull();
        demand.ConversionRequested.ShouldBeFalse();

        var verify = (await services.Planning.NextAsync(claim.Job.Id, demand)).Value.ShouldBeOfType<MediaReconciliationDirective.Verify>();

        verify.Source.RepresentationId.ShouldBe(published.Graph.Id);
        verify.Source.Decoder.ShouldBe(new RomDecoderIdentity.Native(Upgraded(Decoder)));
        var retained = await ArchiveAsync(db);
        retained.Verifications.Single().Id.ShouldBe(published.VerificationId);
        retained.Verifications.Single().Decoder.ShouldBe(new RomDecoderIdentity.Native(Decoder));
        retained.Creations.Single().Id.ShouldBe(published.CreationId);
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(0);
    }

    private (MediaReconciliationPlanning Planning, MediaReconciliationExecution Execution) AutomaticServices(RomdDbContext db,
        RomMediaComposition media, RomChdDecoderBuild decoder, bool canEncode, bool qualified, IMediaRunCustody custody)
    {
        var capabilities = Substitute.For<IMediaHostCapabilities>();
        capabilities.CanDecode(Recipe.Kind).Returns(true);
        capabilities.CanEncode(Recipe.Kind).Returns(canEncode);
        capabilities.Decoder(RomRepresentationEncoding.Chd).Returns(decoder);
        capabilities.Qualifies(Recipe.Kind, decoder.Rid).Returns(qualified);
        capabilities.QualifiedRuntime(Recipe.Kind).Returns(media.QualifiedRuntimes[Recipe.Kind]);
        capabilities.ManagedDecoderBuild.Returns("fixture-managed");
        var profile = Substitute.For<IMediaReconciliationMediaProfile>();
        profile.LimitsFingerprint.Returns(Romd.Domain.Hashing.Sha256.Parse(new string('d', 64)));
        profile.OriginalInspectionDecoder.Returns(new RomDecoderIdentity.Managed("fixture-inspector"));
        profile.Encoder(Recipe.Kind).Returns(new MediaReconciliationEncoder(Encoder.BuildLabel, null));
        var decision = new MediaReconciliationDecision(media.Kinds, media.Reconciliation, capabilities, profile);
        var descriptors = Substitute.For<IRomCdDescriptorIdentityReader>();
        foreach (var (id, bytes) in new[] { (3, originals["disc.cue"]), (5, secondCue) })
        {
            var identity = Identity(bytes);
            descriptors.ReadAsync(id, identity.Sha256, identity.Size, Arg.Any<CancellationToken>()).Returns(identity);
        }
        var planning = new MediaReconciliationPlanning(new MediaReconciliationSnapshotReader(database.CreateOptions(), media.Kinds, descriptors), decision);
        return (planning, new(db, database.CreateOptions(), new EfUnitOfWork(db), custody, TimeProvider.System, media.Kinds, decision));
    }

    private async Task<JobExecutionClaim<MediaReconciliationJob>> ClaimAutomaticAsync(RomdDbContext db, int layoutId)
    {
        Guid job;
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var accepted = await new MediaReconciliationRequests(db, TimeProvider.System).StageSubjectAsync(new(Guid.NewGuid(),
                new MediaReconciliationSubject.OriginalLayout(layoutId), false, MediaReconciliationCause.Import, null, null));
            accepted.IsError.ShouldBeFalse();
            job = accepted.Value.Job.Id;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        var repository = new MediaReconciliationJobRepository(db, TimeProvider.System);
        return (await new ClaimedJobRepository<MediaReconciliationJob, MediaReconciliationJobRepository>(repository, db, TimeProvider.System, new())
            .TryClaimExecutionAsync(job, "automatic-cd-fixture")).ShouldNotBeNull();
    }

    private async Task<RomRepresentationFacts> PublishAutomaticAttemptAsync(RomdDbContext db, MediaReconciliationExecution execution,
        MediaReconciliationAttempt attempt, RomMeasuredRepresentation measured, int fileId, Dictionary<string, int> stored)
    {
        db.Files.Add(new() { Id = fileId, Sha256 = measured.Measurement.ArtifactSha256, Size = measured.Measurement.ArtifactSize, SizeOnDisk = measured.Measurement.ArtifactSize });
        await db.SaveChangesAsync();
        var manifest = (RomReconstructionManifest.CdTracks)measured.Manifest;
        var descriptors = manifest.Members.Select((member, ordinal) => (member, ordinal)).Where(pair => pair.member is RomCdStoredMember)
            .Select(pair => new RomRetainedDescriptor(pair.ordinal, stored[pair.member.Path], pair.member.Identity.Sha256, pair.member.Identity.Size)).ToArray();
        var artifact = Substitute.For<ITempFile>();
        artifact.FileSha256.Returns(measured.Measurement.ArtifactSha256);
        artifact.FileSize.Returns(measured.Measurement.ArtifactSize);
        var originals = manifest.Members.OfType<RomCdStoredMember>().ToDictionary(member => member.Path, member =>
        {
            var file = Substitute.For<ITempFile>();
            file.FileSha256.Returns(member.Identity.Sha256);
            file.FileSize.Returns(member.Identity.Size);
            return file;
        }, StringComparer.Ordinal);
        var encoded = new MediaReconciliationEncoded.Cd(artifact, new(artifact.FileSha256, Md5.Parse(new string('e', 32)), Sha1.Parse(new string('e', 40)), artifact.FileSize), measured, EncodingOf(measured, Encoder), originals, DateTimeOffset.UtcNow);
        await using (var transaction = await new EfUnitOfWork(db).BeginTransactionAsync())
        {
            (await execution.PrepareAsync(attempt, descriptors.Select(row => row.Sha256).Append(measured.Measurement.ArtifactSha256).ToArray())).IsError.ShouldBeFalse();
            (await execution.StageEncodedAsync(attempt, encoded, fileId, descriptors)).IsError.ShouldBeFalse();
            await transaction.CommitAsync();
        }
        return await ArchiveAsync(db, ((MediaReconciliationLayout.Original)attempt.Capture.Sources.Layout!).LayoutId);
    }
}
