using ErrorOr;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Admin.Application.Storage.Files;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Storage;
using Romd.Domain.Source.Rom;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.MediaFacts;
using Romd.Persistence.Repositories;
using Romd.Persistence.Repositories.Reconciliation;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class MediaReconciliationSchemaTests
{
    private async Task<JobExecutionClaim<MediaReconciliationJob>> AcceptExecutionAsync(RomdDbContext db, MediaReconciliationSubject subject, bool conversion)
    {
        Guid id;
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var accepted = await new MediaReconciliationRequests(db, TimeProvider.System).StageSubjectAsync(
                new(Guid.NewGuid(), subject, conversion, MediaReconciliationCause.Readiness, null, null));
            accepted.IsError.ShouldBeFalse();
            id = accepted.Value.Job.Id;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        var repository = new MediaReconciliationJobRepository(db, TimeProvider.System);
        return (await new ClaimedJobRepository<MediaReconciliationJob, MediaReconciliationJobRepository>(repository, db, TimeProvider.System, new())
            .TryClaimExecutionAsync(id, "execution-fixture")).ShouldNotBeNull();
    }
    private static IMediaRunCustody StoppedCustody()
    {
        var custody = Substitute.For<IMediaRunCustody>();
        custody.EnsureJobStoppedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Result.Success);
        return custody;
    }
    private MediaReconciliationExecution Execution(RomdDbContext db, IMediaRunCustody custody, MediaReconciliationDecision decision) =>
        new(db, _database.CreateOptions(), new EfUnitOfWork(db), custody, TimeProvider.System, RomMediaPolicyComposition.Create().Kinds, decision);
    private static readonly RomChdEncoderBuild PublicationEncoder = new("linux-arm64", "mame0280", MediaFactGraphFixture.Hash('e'), 1);
    private static MediaReconciliationDecision PolicyDecision(RomChdDecoderBuild decoder, bool canEncode)
    {
        var composition = RomMediaPolicyComposition.Create();
        var capabilities = Substitute.For<Romd.Application.Common.Content.IMediaHostCapabilities>();
        capabilities.CanDecode(Arg.Any<string>()).Returns(true);
        capabilities.CanEncode(Arg.Any<string>()).Returns(canEncode);
        capabilities.ManagedDecoderBuild.Returns("fixture-managed");
        capabilities.Decoder(RomRepresentationEncoding.Chd).Returns(decoder);
        capabilities.QualifiedRuntime(Arg.Any<string>()).Returns(decoder.Rid);
        capabilities.Qualifies(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        var profile = Substitute.For<IMediaReconciliationMediaProfile>();
        profile.LimitsFingerprint.Returns(MediaFactGraphFixture.Hash('b'));
        profile.OriginalInspectionDecoder.Returns(new RomDecoderIdentity.Managed("fixture-inspector"));
        profile.Encoder("ps1-cd-set").Returns(new MediaReconciliationEncoder(PublicationEncoder.BuildLabel, null));
        profile.Encoder("gamecube-disc").Returns(new MediaReconciliationEncoder("fixture-gc-encoder", RomIsoDiscKind.GameCube));
        return new(composition.Kinds, composition.Reconciliation, capabilities, profile);
    }
    private static MediaReconciliationCapture VerifyCapture(Guid job, long version, RomRepresentationFacts graph, RomChdDecoderBuild decoder, char limit) =>
        new(job, version, new([new MediaReconciliationInput.Artifact(graph, null, null)], null, [], null), MediaReconciliationOperation.Verify, null,
            MediaFactGraphFixture.Hash('a'), MediaFactGraphFixture.Hash(limit), new RomDecoderIdentity.Native(decoder), null, null, null);
    private static RomMediaVerificationMeasurement.Cd CdMeasurement(RomRepresentationFacts graph, RomChdDecoderBuild decoder, RomPs1CdBoot? boot) => new(
        new(graph.Container, graph.Manifest, Measurement(graph, decoder)), "fixture-cd-observer", boot,
        new(graph.ArtifactSha256, Romd.Domain.Hashing.Md5.Parse(new string('e', 32)), MediaFactGraphFixture.Short('e'), graph.ArtifactSize));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execution_Verification_RetainsAllBuildsAndRawFactsBeforeIndependentTerminalJournal(bool observeBoot)
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var claim = await AcceptExecutionAsync(db, new MediaReconciliationSubject.Representation(graph.Cd.Id), false);
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        var execution = Execution(db, StoppedCustody(), PolicyDecision(TargetDecoder, true));
        var captured = await execution.CaptureAsync(VerifyCapture(claim.Job.Id, 1, graph.Cd, TargetDecoder, 'b'));
        captured.IsError.ShouldBeFalse();
        captured.Value.Number.ShouldBe(1);
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(1);
        (await execution.RecordVerificationAsync(captured.Value, CdMeasurement(graph.Cd, TargetDecoder, observeBoot ? MediaFactGraphFixture.Boot : null), processTreeStopped: true)).IsError.ShouldBeFalse();
        var retained = (await new MediaFactReader(db).ReadAsync([graph.Cd.Id]))[graph.Cd.Id];
        retained.Verifications.Select(proof => proof.Decoder).ShouldBe([graph.Cd.Verifications.Single().Decoder, new RomDecoderIdentity.Native(TargetDecoder)], ignoreOrder: true);
        retained.Verifications.Single(proof => proof.Id == graph.Cd.Verifications.Single().Id).Outputs.ShouldBe(graph.Cd.Verifications.Single().Outputs);
        if (observeBoot)
            retained.CdBootInspections.Single().Boot.ShouldBe(MediaFactGraphFixture.Boot);
        else
            retained.CdBootInspections.ShouldBeEmpty();
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(0);
        (await execution.ReadOutcomeAsync(claim.Job.Id)).ShouldBeNull();
        var finished = await execution.FinishAsync(claim.Job.Id, 1, MediaReconciliationOutcomeKind.Completed, "verified", processTreeStopped: true);
        finished.IsError.ShouldBeFalse();
        finished.Value.Kind.ShouldBe(MediaReconciliationOutcomeKind.Completed);
        var replay = (await execution.ReadOutcomeAsync(claim.Job.Id)).ShouldNotBeNull();
        replay.ShouldBe(finished.Value);
        (await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM romd.\"MediaReconciliationInputs\" WHERE \"JobId\"={claim.Job.Id}").SingleAsync()).ShouldBe(1);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Execution_Verification_CannotLoseOrContradictAnyRetainedPositiveBootObservation(bool contradictoryBoot, bool arrivedAfterCapture)
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await new MediaCreationFactWriter(db).StageCdBootAsync(graph.Cd.Id, graph.Cd.Verifications.Single().Id,
                "earlier-observer", DateTimeOffset.UnixEpoch, MediaFactGraphFixture.Boot, default);
            await transaction.CommitAsync();
        }
        var original = (await new MediaFactReader(db).ReadAsync([graph.Cd.Id]))[graph.Cd.Id];
        var claim = await AcceptExecutionAsync(db, new MediaReconciliationSubject.Representation(original.Id), false);
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        var execution = Execution(db, StoppedCustody(), PolicyDecision(TargetDecoder, true));
        var captured = await execution.CaptureAsync(VerifyCapture(claim.Job.Id, 1, original, TargetDecoder, 'b'));
        captured.IsError.ShouldBeFalse();
        var before = MediaFactGraphFixture.Boot;
        RomPs1CdBoot? boot = contradictoryBoot ? new(before.Profile, before.FirstTrackIndex01, before.VolumeBlocks, before.RootBlock, before.RootBytes,
            "OTHER.EXE;1", before.BootBlock, before.BootBytes, before.EntryPoint, before.LoadAddress, before.LoadBytes) : null;
        if (arrivedAfterCapture)
        {
            // A later observer appends a contradictory positive after durable capture. The old selected boot/fingerprint is unchanged.
            await using var transaction = await db.Database.BeginTransactionAsync();
            await new MediaCreationFactWriter(db).StageCdBootAsync(original.Id, original.Verifications.Single().Id,
                "later-observer", DateTimeOffset.UnixEpoch.AddTicks(1), boot.ShouldNotBeNull(), default);
            await transaction.CommitAsync();
            boot = before;
        }
        var expected = (await new MediaFactReader(db).ReadAsync([original.Id]))[original.Id];
        (await execution.RecordVerificationAsync(captured.Value, CdMeasurement(original, TargetDecoder, boot), processTreeStopped: true))
            .FirstError.ShouldBe(MediaReconciliationErrors.Measurement);
        var retained = (await new MediaFactReader(db).ReadAsync([original.Id]))[original.Id];
        var oldProof = original.Verifications.Single();
        var retainedProof = retained.Verifications.Single();
        (retainedProof.Id, retainedProof.ArtifactSha256, retainedProof.ArtifactSize, retainedProof.Decoder, retainedProof.CheckedProfile,
            retainedProof.VerifierBuild, retainedProof.ObservedAt, retainedProof.ObservedParametersSha256, retainedProof.ComputedRawSha1, retainedProof.ComputedOverallSha1)
            .ShouldBe((oldProof.Id, oldProof.ArtifactSha256, oldProof.ArtifactSize, oldProof.Decoder, oldProof.CheckedProfile,
                oldProof.VerifierBuild, oldProof.ObservedAt, oldProof.ObservedParametersSha256, oldProof.ComputedRawSha1, oldProof.ComputedOverallSha1));
        retainedProof.Outputs.ShouldBe(oldProof.Outputs);
        retained.CdBootInspections.Select(observation => (observation.Id, observation.VerificationId, observation.InspectorBuild, observation.ObservedAt, observation.Boot))
            .ShouldBe(expected.CdBootInspections.Select(observation => (observation.Id, observation.VerificationId, observation.InspectorBuild, observation.ObservedAt, observation.Boot)));
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(1);
        (await execution.ReadAttemptOutcomeAsync(claim.Job.Id, captured.Value.Number)).ShouldBeNull();
        (await execution.ReadOutcomeAsync(claim.Job.Id)).ShouldBeNull();
    }

    [Fact]
    public async Task Execution_UnknownCustody_NeverCapturesOrReleasesSources()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var claim = await AcceptExecutionAsync(db, new MediaReconciliationSubject.Representation(graph.Cd.Id), false);
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        var custody = StoppedCustody();
        var execution = Execution(db, custody, PolicyDecision(TargetDecoder, true));
        var request = VerifyCapture(claim.Job.Id, 1, graph.Cd, TargetDecoder, 'b');
        custody.EnsureJobStoppedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Error.Conflict("fixture.running", "still running"));
        (await execution.CaptureAsync(request)).FirstError.ShouldBe(MediaReconciliationErrors.Custody);
        (await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM romd.\"MediaReconciliationAttempts\" WHERE \"JobId\"={claim.Job.Id}").SingleAsync()).ShouldBe(0);
        custody.EnsureJobStoppedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Result.Success);
        var captured = await execution.CaptureAsync(request);
        captured.IsError.ShouldBeFalse();
        custody.EnsureJobStoppedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Error.Conflict("fixture.running", "still running"));
        (await execution.FailAttemptAsync(captured.Value, "decoder-failed", deterministic: true, processTreeStopped: true)).FirstError.ShouldBe(MediaReconciliationErrors.Custody);
        (await execution.SupersedeAttemptAsync(claim.Job.Id, "replanned", processTreeStopped: true)).FirstError.ShouldBe(MediaReconciliationErrors.Custody);
        (await execution.RecordVerificationAsync(captured.Value, CdMeasurement(graph.Cd, TargetDecoder, MediaFactGraphFixture.Boot), processTreeStopped: true)).FirstError.ShouldBe(MediaReconciliationErrors.Custody);
        (await execution.FinishAsync(claim.Job.Id, 1, MediaReconciliationOutcomeKind.Cancelled, "cancelled", processTreeStopped: true)).FirstError.ShouldBe(MediaReconciliationErrors.Custody);
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(1);
        (await new MediaFactReader(db).ReadAsync([graph.Cd.Id]))[graph.Cd.Id].Verifications.Count.ShouldBe(1);
        (await execution.ReadOutcomeAsync(claim.Job.Id)).ShouldBeNull();
    }

    [Fact]
    public async Task Execution_ResumeUnderNewFence_ReusesExactAttemptAndRetainsItsOriginalInputHistory()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var claim = await AcceptExecutionAsync(db, new MediaReconciliationSubject.Representation(graph.Cd.Id), false);
        var execution = Execution(db, StoppedCustody(), PolicyDecision(TargetDecoder, true));
        var request = VerifyCapture(claim.Job.Id, 1, graph.Cd, TargetDecoder, 'b');
        using (JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System))
            (await execution.CaptureAsync(request)).IsError.ShouldBeFalse();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE romd.\"Jobs\" SET \"ExecutionLeaseExpiresAtUtc\"=0 WHERE \"Id\"={claim.Job.Id}");
        var resumed = (await new ClaimedJobRepository<MediaReconciliationJob, MediaReconciliationJobRepository>(new(db, TimeProvider.System), db, TimeProvider.System, new())
            .TryClaimExecutionAsync(claim.Job.Id, "replacement-worker")).ShouldNotBeNull();
        using var nextFence = JobExecutionFenceScope.Enter(resumed.Job.Id, resumed.FenceToken, TimeProvider.System);
        var captured = await execution.CaptureAsync(request);
        captured.IsError.ShouldBeFalse();
        captured.Value.Number.ShouldBe(1);
        (await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM romd.\"MediaReconciliationAttempts\" WHERE \"JobId\"={claim.Job.Id}").SingleAsync()).ShouldBe(1);
        (await db.Database.SqlQuery<Guid>($"SELECT \"ExecutionFenceToken\" AS \"Value\" FROM romd.\"MediaReconciliationAttempts\" WHERE \"JobId\"={claim.Job.Id}").SingleAsync()).ShouldBe(resumed.FenceToken);
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(1);
        using (JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System))
            await Should.ThrowAsync<JobExecutionOwnershipLostException>(() => execution.FailAttemptAsync(captured.Value, "stale", false, true));
    }

    [Theory]
    [InlineData("Rom.Media.Startup")]
    [InlineData("Rom.Media.Crash")]
    [InlineData("Rom.Media.Protocol")]
    [InlineData("Rom.Media.IoFailure")]
    [InlineData("Rom.Media.OutputLimit")]
    [InlineData("Rom.Media.CpuDeadline")]
    [InlineData("Rom.Media.WallDeadline")]
    public async Task Execution_StoppedTransientFailure_ReleasesPinsAndAllowsTheIdenticalAttemptWithoutCachedFailure(string code)
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var claim = await AcceptExecutionAsync(db, new MediaReconciliationSubject.Representation(graph.Cd.Id), conversion: false);
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        var execution = Execution(db, StoppedCustody(), PolicyDecision(TargetDecoder, true));
        var capture = VerifyCapture(claim.Job.Id, 1, graph.Cd, TargetDecoder, 'b');
        var first = await execution.CaptureAsync(capture);
        first.IsError.ShouldBeFalse();
        (await execution.FailAttemptAsync(first.Value, code, deterministic: false, processTreeStopped: true)).IsError.ShouldBeFalse();
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(0);
        (await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM romd.\"MediaReconciliationFailures\"").SingleAsync()).ShouldBe(0);
        var next = await execution.CaptureAsync(capture);
        next.IsError.ShouldBeFalse();
        next.Value.Number.ShouldBe(first.Value.Number + 1);
        next.Value.PriorFailure.ShouldBeNull();
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(1);
        var demand = (await execution.ReadDemandAsync(claim.Job.Id)).ShouldNotBeNull();
        demand.Version.ShouldBe(1);
        (await db.Set<MediaReconciliationOutcomeEntity>().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Execution_DeterministicFailure_UsesFullDecoderAndLimitsIdentityWhenReplanned()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var claim = await AcceptExecutionAsync(db, new MediaReconciliationSubject.Representation(graph.Cd.Id), false);
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        var execution = Execution(db, StoppedCustody(), PolicyDecision(TargetDecoder, true));
        var request = VerifyCapture(claim.Job.Id, 1, graph.Cd, TargetDecoder, 'b');
        var first = await execution.CaptureAsync(request);
        first.IsError.ShouldBeFalse();
        first.Value.PriorFailure.ShouldBeNull();
        (await execution.FailAttemptAsync(first.Value, "exact-decoder-failure", deterministic: true, processTreeStopped: true)).IsError.ShouldBeFalse();
        var same = await execution.CaptureAsync(request);
        same.IsError.ShouldBeFalse();
        same.Value.Number.ShouldBe(2);
        same.Value.PriorFailure.ShouldBe(new(claim.Job.Id, 1, "exact-decoder-failure"));
        var changedLimits = await execution.CaptureAsync(VerifyCapture(claim.Job.Id, 1, graph.Cd, TargetDecoder, 'c'));
        changedLimits.IsError.ShouldBeFalse();
        changedLimits.Value.Number.ShouldBe(3);
        changedLimits.Value.PriorFailure.ShouldBeNull();
        var differentDecoder = new RomChdDecoderBuild(TargetDecoder.Rid, TargetDecoder.UpstreamCommit, TargetDecoder.RecipeVersion + 1);
        var changedDecoder = await execution.CaptureAsync(VerifyCapture(claim.Job.Id, 1, graph.Cd, differentDecoder, 'b'));
        changedDecoder.IsError.ShouldBeFalse();
        changedDecoder.Value.Number.ShouldBe(4);
        changedDecoder.Value.PriorFailure.ShouldBeNull();
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(1);
        (await db.Database.SqlQuery<string>($"SELECT \"Outcome\" AS \"Value\" FROM romd.\"MediaReconciliationAttempts\" WHERE \"JobId\"={claim.Job.Id} AND \"Number\"=2").SingleAsync()).ShouldBe("Superseded");
    }

    [Fact]
    public async Task Execution_NewerDemand_RejectsOldCaptureAndTerminalAcknowledgement()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var subject = new MediaReconciliationSubject.LogicalRom(graph.RomId);
        var claim = await AcceptExecutionAsync(db, subject, false);
        await using (var requester = _database.CreateContext())
        await using (var transaction = await requester.Database.BeginTransactionAsync())
        {
            (await new MediaReconciliationRequests(requester, TimeProvider.System).StageSubjectAsync(new(Guid.NewGuid(), subject, false, MediaReconciliationCause.Facts, null, null))).IsError.ShouldBeFalse();
            await requester.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        var execution = Execution(db, StoppedCustody(), PolicyDecision(TargetDecoder, true));
        (await execution.CaptureAsync(VerifyCapture(claim.Job.Id, 1, graph.Cd, TargetDecoder, 'b'))).FirstError.ShouldBe(MediaReconciliationErrors.Changed);
        (await execution.FinishAsync(claim.Job.Id, 1, MediaReconciliationOutcomeKind.Completed, "no-work", true)).FirstError.ShouldBe(MediaReconciliationErrors.Changed);
        (await execution.ReadDemandAsync(claim.Job.Id)).ShouldNotBeNull().Version.ShouldBe(2);
        (await execution.ReadOutcomeAsync(claim.Job.Id)).ShouldBeNull();
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Execution_LogicalWithdrawal_AfterAdmissionStillAllowsExactCompleteCdMeasurement()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var claim = await AcceptExecutionAsync(db, new MediaReconciliationSubject.Representation(graph.Cd.Id), false);
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        var execution = Execution(db, StoppedCustody(), PolicyDecision(TargetDecoder, true));
        var captured = await execution.CaptureAsync(VerifyCapture(claim.Job.Id, 1, graph.Cd, TargetDecoder, 'b'));
        captured.IsError.ShouldBeFalse();
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM romd.\"Roms\" WHERE \"Id\"={graph.RomId}");
        (await execution.RecordVerificationAsync(captured.Value, CdMeasurement(graph.Cd, TargetDecoder, MediaFactGraphFixture.Boot), true)).IsError.ShouldBeFalse();
        var retained = (await new MediaFactReader(db).ReadAsync([graph.Cd.Id]))[graph.Cd.Id];
        retained.Outputs.All(output => output.RomId is null).ShouldBeTrue();
        retained.Verifications.Count.ShouldBe(2);
        retained.CdBootInspections.Single().Boot.ShouldBe(MediaFactGraphFixture.Boot);
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(0);
        (await execution.FinishAsync(claim.Job.Id, 1, MediaReconciliationOutcomeKind.Superseded, "subject-withdrawn", true)).IsError.ShouldBeFalse();
    }

    private static async Task<MediaReconciliationSources> LayoutSourcesAsync(RomdDbContext db, MediaFactGraphFixture graph)
    {
        db.Platforms.Add(new() { Id = 1, Name = "PlayStation", Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd, BuiltInVersion = 1, BaseName = "PlayStation", BaseCompactLabel = "PS", ShortName = "psx", CanonicalKey = "psx" });
        db.Titles.Add(new() { Id = 1, PlatformId = 1, Name = "Test", NormalizedName = "test", EnrichmentStatus = "Completed" });
        db.CatalogReleases.Add(new() { Id = 1, PlatformId = 1, CatalogTitleId = 1, Fingerprint = "fixture-layout", Name = "Test" });
        Guid revision = Guid.NewGuid();
        db.OriginalDiscLayouts.Add(new()
        {
            Id = 1,
            Revision = revision,
            SourceJobId = Guid.NewGuid(),
            SourceRelativePath = "set/disc.cue",
            EntryPoint = "set/disc.cue",
            DescriptorFileId = 3,
            DependencyPathsJson = "[\"set/disc.bin\"]",
            Files = [new() { Path = "set/disc.bin", RomId = graph.RomId, FileId = graph.Source.FileId }]
        });
        await db.SaveChangesAsync();
        await AddLayoutCatalogAsync(db, graph);
        var cue = ((RomReconstructionManifest.CdTracks)graph.Cd.Manifest).Members.OfType<RomCdStoredMember>().Single();
        return new([new MediaReconciliationInput.Logical(MediaReconciliationSourceRole.Bin,"disc.bin",graph.Source,graph.Source.Outputs.Single().Id,graph.Source.Verifications.Single().Id),
            new MediaReconciliationInput.Stored(MediaReconciliationSourceRole.Cue,"disc.cue",3,cue.Identity)], new MediaReconciliationLayout.Original(1, revision, 1, 1, "fixture-layout", "disc.cue"), [], null);
    }
    private static MediaReconciliationCapture EncodeCapture(Guid job, MediaReconciliationSources sources) =>
        PolicyDecision(TargetDecoder, true).Decide(job, new(sources.Layout!.Subject, 1, true, false),
            new MediaReconciliationFacts.Layout("ps1-cd-set", sources, [], null, false, null)).Value.ShouldBeOfType<MediaReconciliationDirective.Encode>().Capture;

    [Fact]
    public async Task Execution_ImportedEligiblePs1Layout_WithoutExplicitConversionIntent_CapturesEncodeAndPinsOriginalSources()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var sources = await LayoutSourcesAsync(db, graph);
        var subject = new MediaReconciliationSubject.OriginalLayout(1);
        Guid jobId;
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var accepted = await new MediaReconciliationRequests(db, TimeProvider.System).StageSubjectAsync(
                new(Guid.NewGuid(), subject, conversionRequested: false, MediaReconciliationCause.Import, null, null));
            accepted.IsError.ShouldBeFalse();
            jobId = accepted.Value.Job.Id;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        var repository = new MediaReconciliationJobRepository(db, TimeProvider.System);
        var claim = (await new ClaimedJobRepository<MediaReconciliationJob, MediaReconciliationJobRepository>(
            repository, db, TimeProvider.System, new()).TryClaimExecutionAsync(jobId, "automatic-import-fixture")).ShouldNotBeNull();
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        var decision = PolicyDecision(TargetDecoder, canEncode: true);
        var execution = Execution(db, StoppedCustody(), decision);
        var demand = (await execution.ReadDemandAsync(jobId)).ShouldNotBeNull();
        demand.Subject.ShouldBe(subject);
        demand.ConversionRequested.ShouldBeFalse();

        var planned = await new MediaReconciliationPlanning(SnapshotReader(graph), decision).NextAsync(jobId, demand);
        planned.IsError.ShouldBeFalse();
        var encode = planned.Value.ShouldBeOfType<MediaReconciliationDirective.Encode>();
        encode.Capture.KindId.ShouldBe("ps1-cd-set");
        encode.Capture.Identity.Operation.ShouldBe(MediaReconciliationOperation.Encode);
        encode.Capture.EncoderBuild.ShouldBe(PublicationEncoder.BuildLabel);
        encode.Capture.Decoder.ShouldBe(new RomDecoderIdentity.Native(TargetDecoder));
        encode.Capture.Sources.Fingerprint.ShouldBe(sources.Fingerprint);

        var captured = await execution.CaptureAsync(encode.Capture);
        captured.IsError.ShouldBeFalse();
        var pins = await db.Set<MediaReconciliationPinEntity>().OrderBy(pin => pin.Ordinal).ToArrayAsync();
        pins.Select(pin => pin.FileId).ShouldBe([graph.Source.FileId, 3]);
        pins[0].RepresentationId.ShouldBe(graph.Source.Id);
        pins[0].OutputId.ShouldBe(graph.Source.Outputs.Single().Id);
        pins[0].VerificationId.ShouldBe(graph.Source.Verifications.Single().Id);
        (await new MediaFactReader(db).ReadAsync([graph.Source.Id]))[graph.Source.Id].Verifications
            .Select(proof => proof.Id).ShouldBe(graph.Source.Verifications.Select(proof => proof.Id));

        (await execution.FinishAsync(jobId, demand.Version, MediaReconciliationOutcomeKind.Cancelled,
            "test-stopped-before-encoding", processTreeStopped: true)).IsError.ShouldBeFalse();
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Execution_CompleteBinAndCueVector_PinsEveryActualDependencyInOneCapture()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var sources = await LayoutSourcesAsync(db, graph);
        var claim = await AcceptExecutionAsync(db, new MediaReconciliationSubject.OriginalLayout(1), true);
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        var captured = await Execution(db, StoppedCustody(), PolicyDecision(TargetDecoder, true)).CaptureAsync(EncodeCapture(claim.Job.Id, sources));
        captured.IsError.ShouldBeFalse();
        captured.Value.Capture.Sources.Fingerprint.ShouldBe(sources.Fingerprint);
        var pins = await db.Set<MediaReconciliationPinEntity>().OrderBy(pin => pin.Ordinal).ToArrayAsync();
        pins.Select(pin => pin.FileId).ShouldBe([graph.Source.FileId, 3]);
        pins[0].RepresentationId.ShouldBe(graph.Source.Id);
        pins[0].OutputId.ShouldBe(graph.Source.Outputs.Single().Id);
        pins[0].VerificationId.ShouldBe(graph.Source.Verifications.Single().Id);
        pins[1].RepresentationId.ShouldBeNull();
        pins[1].OutputId.ShouldBeNull();
        (await db.Database.SqlQuery<int>($"SELECT \"SourceCount\"::integer AS \"Value\" FROM romd.\"MediaReconciliationAttempts\" WHERE \"JobId\"={claim.Job.Id}").SingleAsync()).ShouldBe(2);
    }

    [Theory]
    [InlineData("cue")]
    [InlineData("backing")]
    [InlineData("revision")]
    [InlineData("catalog")]
    public async Task Execution_ChangedLayoutAssociation_RollsBackEveryInputAndPin(string change)
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var sources = await LayoutSourcesAsync(db, graph);
        var claim = await AcceptExecutionAsync(db, new MediaReconciliationSubject.OriginalLayout(1), true);
        if (change == "cue")
            await db.OriginalDiscLayouts.Where(row => row.Id == 1).ExecuteUpdateAsync(set => set.SetProperty(row => row.DescriptorFileId, graph.Cd.FileId));
        else if (change == "backing")
            await db.OriginalDiscLayoutFiles.ExecuteUpdateAsync(set => set.SetProperty(row => row.FileId, graph.Cd.FileId));
        else if (change == "revision")
            await db.OriginalDiscLayouts.Where(row => row.Id == 1).ExecuteUpdateAsync(set => set.SetProperty(row => row.Revision, Guid.NewGuid()));
        else
            await db.CatalogReleases.Where(row => row.Id == 1).ExecuteUpdateAsync(set => set.SetProperty(row => row.Fingerprint, "changed"));
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        (await Execution(db, StoppedCustody(), PolicyDecision(TargetDecoder, true)).CaptureAsync(EncodeCapture(claim.Job.Id, sources))).FirstError.ShouldBe(MediaReconciliationErrors.Changed);
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(0);
        (await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM romd.\"MediaReconciliationInputs\" WHERE \"JobId\"={claim.Job.Id}").SingleAsync()).ShouldBe(0);
        (await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM romd.\"MediaReconciliationAttempts\" WHERE \"JobId\"={claim.Job.Id}").SingleAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execution_NewMatchingCatalogCandidate_RejectsCapturedEncodingForPendingAndAcceptedLayouts(bool accepted)
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var sources = await LayoutSourcesAsync(db, graph);
        if (accepted)
        {
            await db.OriginalDiscLayouts.ExecuteUpdateAsync(set => set.SetProperty(row => row.State, "accepted").SetProperty(row => row.PlatformId, 1)
                .SetProperty(row => row.ReleaseId, 1).SetProperty(row => row.Fingerprint, "fixture-layout"));
            await db.OriginalDiscLayoutFiles.ExecuteUpdateAsync(set => set.SetProperty(row => row.BindingRevision, sources.Layout!.Revision));
        }
        var claim = await AcceptExecutionAsync(db, new MediaReconciliationSubject.OriginalLayout(1), true);
        var capture = EncodeCapture(claim.Job.Id, sources);
        db.CatalogReleases.Add(new() { Id = 2, PlatformId = 1, CatalogTitleId = 1, Fingerprint = "second-candidate", Name = "Other" });
        var identity = graph.Source.Outputs.Single().Identity;
        db.CatalogReleaseFiles.Add(new() { CatalogReleaseId = 2, FileFingerprint = "other-bin", Name = "other.bin", Size = identity.Size, Sha1 = identity.Sha1 });
        await db.SaveChangesAsync();
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        (await Execution(db, StoppedCustody(), PolicyDecision(TargetDecoder, true)).CaptureAsync(capture)).FirstError.ShouldBe(MediaReconciliationErrors.Changed);
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(0);
        (await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM romd.\"MediaReconciliationAttempts\" WHERE \"JobId\"={claim.Job.Id}").SingleAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execution_CurrentEncoderUnavailableOrDecoderChanged_RejectsTheEarlierPolicyCapture(bool decoderChanged)
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var sources = await LayoutSourcesAsync(db, graph);
        var claim = await AcceptExecutionAsync(db, new MediaReconciliationSubject.OriginalLayout(1), true);
        var capture = EncodeCapture(claim.Job.Id, sources);
        var currentDecoder = decoderChanged ? new RomChdDecoderBuild(TargetDecoder.Rid, TargetDecoder.UpstreamCommit, TargetDecoder.RecipeVersion + 1) : TargetDecoder;
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        (await Execution(db, StoppedCustody(), PolicyDecision(currentDecoder, decoderChanged)).CaptureAsync(capture)).FirstError.ShouldBe(MediaReconciliationErrors.Changed);
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Execution_PublicationFailureAfterProofAndBoot_RollsBackFactsChecksumsAndSourceRelease()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var claim = await AcceptExecutionAsync(db, new MediaReconciliationSubject.Representation(graph.Cd.Id), false);
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        var execution = Execution(db, StoppedCustody(), PolicyDecision(TargetDecoder, true));
        var captured = await execution.CaptureAsync(VerifyCapture(claim.Job.Id, 1, graph.Cd, TargetDecoder, 'b'));
        captured.IsError.ShouldBeFalse();
        // Inject failure after the publisher has staged the complete proof and raw CD observation.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION romd.fail_reconciliation_publication_fixture() RETURNS trigger LANGUAGE plpgsql AS $fixture$
            BEGIN
                IF NEW."Outcome"='Recorded' THEN RAISE EXCEPTION 'fixture publication failed' USING ERRCODE='P0001'; END IF;
                RETURN NEW;
            END $fixture$;
            CREATE TRIGGER "ZZ_FailReconciliationPublicationFixture" BEFORE UPDATE ON romd."MediaReconciliationAttempts"
                FOR EACH ROW EXECUTE FUNCTION romd.fail_reconciliation_publication_fixture();
            """);
        var error = await Should.ThrowAsync<Npgsql.PostgresException>(() => execution.RecordVerificationAsync(captured.Value, CdMeasurement(graph.Cd, TargetDecoder, MediaFactGraphFixture.Boot), true));
        error.SqlState.ShouldBe("P0001");
        var retained = (await new MediaFactReader(db).ReadAsync([graph.Cd.Id]))[graph.Cd.Id];
        retained.Verifications.Select(proof => proof.Id).ShouldBe([graph.Cd.Verifications.Single().Id]);
        retained.CdBootInspections.ShouldBeEmpty();
        var file = await db.Files.SingleAsync(row => row.Id == graph.Cd.FileId);
        file.ArtifactMd5.ShouldBeNull();
        file.ArtifactSha1.ShouldBeNull();
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(1);
        (await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM romd.\"MediaReconciliationAttempts\" WHERE \"JobId\"={claim.Job.Id} AND \"Outcome\" IS NULL").SingleAsync()).ShouldBe(1);
        (await execution.ReadOutcomeAsync(claim.Job.Id)).ShouldBeNull();
    }

    [Fact]
    public async Task Execution_OriginalInspection_RecordsActualSourceWitnessAndWakesCurrentDemandAtomically()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var claim = await AcceptExecutionAsync(db, new MediaReconciliationSubject.LogicalRom(graph.RomId), false);
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        var sourceProof = graph.Source.Verifications.Single();
        var output = graph.Source.Outputs.Single();
        var sources = new MediaReconciliationSources([new MediaReconciliationInput.Logical(MediaReconciliationSourceRole.Whole, "disc.bin", graph.Source, output.Id, sourceProof.Id)], null, [],
            MediaReconciliationReferenceFingerprint.Compute([]));
        var capture = new MediaReconciliationCapture(claim.Job.Id, 1, sources, MediaReconciliationOperation.Inspect, null, MediaFactGraphFixture.Hash('a'), MediaFactGraphFixture.Hash('b'),
            sourceProof.Decoder, null, null, null);
        var execution = Execution(db, StoppedCustody(), PolicyDecision(TargetDecoder, true));
        var captured = await execution.CaptureAsync(capture);
        captured.IsError.ShouldBeFalse();
        var observed = new RomLogicalInspection(Guid.NewGuid(), output.Identity, "fixture/logical/v1", "fixture-observer", DateTimeOffset.UtcNow, new byte[32]);
        var proof = new RomRepresentationVerification(Guid.NewGuid(), graph.Source.ArtifactSha256, graph.Source.ArtifactSize, sourceProof.Decoder,
            "identity/v1", "fixture-verifier", DateTimeOffset.UtcNow, sourceProof.Outputs, graph.Source.Container);
        var measured = new RomMediaVerificationMeasurement.Whole(new(graph.Source.Container, graph.Source.Manifest, proof), observed,
            new(graph.Source.ArtifactSha256, output.Identity.Md5, output.Identity.Sha1, graph.Source.ArtifactSize));
        (await execution.RecordVerificationAsync(captured.Value, measured, true)).IsError.ShouldBeFalse();
        var demand = (await execution.ReadDemandAsync(claim.Job.Id)).ShouldNotBeNull();
        demand.Version.ShouldBe(2);
        demand.ConversionRequested.ShouldBeFalse();
        (await db.Set<MediaReconciliationRequestEntity>().Where(row => row.JobId == claim.Job.Id && row.Cause == "Facts").SingleAsync()).Id.ShouldBe(observed.Id);
        (await db.Database.SqlQuery<Guid>($"SELECT \"ResultInspectionId\" AS \"Value\" FROM romd.\"MediaReconciliationAttempts\" WHERE \"JobId\"={claim.Job.Id}").SingleAsync()).ShouldBe(observed.Id);
        (await db.Database.SqlQuery<Guid>($"SELECT \"SourceVerificationId\" AS \"Value\" FROM romd.\"LogicalInspections\" WHERE \"Id\"={observed.Id}").SingleAsync()).ShouldBe(sourceProof.Id);
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(0);
        (await execution.FinishAsync(claim.Job.Id, 1, MediaReconciliationOutcomeKind.Completed, "old-plan", true)).FirstError.ShouldBe(MediaReconciliationErrors.Changed);
        (await execution.FinishAsync(claim.Job.Id, 2, MediaReconciliationOutcomeKind.Completed, "observed", true)).IsError.ShouldBeFalse();
    }

    [Fact]
    public async Task Execution_TerminalReplay_PrecedesStalePayloadFenceAndCustodyValidation()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var claim = await AcceptExecutionAsync(db, new MediaReconciliationSubject.Representation(graph.Cd.Id), false);
        var custody = StoppedCustody();
        var execution = Execution(db, custody, PolicyDecision(TargetDecoder, true));
        MediaReconciliationJournalOutcome finished;
        using (JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System))
        {
            var result = await execution.FinishAsync(claim.Job.Id, 1, MediaReconciliationOutcomeKind.Superseded, "subject-withdrawn", true);
            result.IsError.ShouldBeFalse();
            finished = result.Value;
        }
        custody.ClearReceivedCalls();
        var replay = await execution.FinishAsync(claim.Job.Id, -1, (MediaReconciliationOutcomeKind)999, "", processTreeStopped: false);
        replay.IsError.ShouldBeFalse();
        replay.Value.ShouldBe(finished);
        await custody.DidNotReceive().EnsureJobStoppedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Execution_CompletionRacesAcceptedDemand_ObservesTheCommittedVersionWithoutDeadlockOrLostRequest()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var subject = new MediaReconciliationSubject.LogicalRom(graph.RomId);
        var claim = await AcceptExecutionAsync(db, subject, false);
        await using var requester = _database.CreateContext();
        await using var requestTransaction = await requester.Database.BeginTransactionAsync();
        var accepted = await new MediaReconciliationRequests(requester, TimeProvider.System).StageSubjectAsync(
            new(Guid.NewGuid(), subject, false, MediaReconciliationCause.Facts, null, null));
        accepted.IsError.ShouldBeFalse();
        accepted.Value.Created.ShouldBeFalse();
        // The acceptor holds the subject advisory lock; the finisher has its root fence before waiting on that lock.
        await using var worker = _database.CreateContext();
        var reachedRoot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new EfUnitOfWork(worker);
        var signalled = Substitute.For<Romd.Admin.Application.Common.Persistence.IUnitOfWork>();
        signalled.BeginTransactionAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var transaction = await inner.BeginTransactionAsync(call.Arg<CancellationToken>());
            reachedRoot.TrySetResult();
            return transaction;
        });
        var execution = new MediaReconciliationExecution(worker, _database.CreateOptions(), signalled, StoppedCustody(), TimeProvider.System, RomMediaPolicyComposition.Create().Kinds, PolicyDecision(TargetDecoder, true));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var finish = Task.Run(async () =>
        {
            using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
            return await execution.FinishAsync(claim.Job.Id, 1, MediaReconciliationOutcomeKind.Completed, "old-plan", true, deadline.Token);
        });
        await reachedRoot.Task.WaitAsync(deadline.Token);
        await requester.SaveChangesAsync(deadline.Token);
        await requestTransaction.CommitAsync(deadline.Token);
        var result = await finish.WaitAsync(deadline.Token);
        result.FirstError.ShouldBe(MediaReconciliationErrors.Changed);
        var current = (await execution.ReadDemandAsync(claim.Job.Id)).ShouldNotBeNull();
        current.Version.ShouldBe(2);
        (await db.Set<MediaReconciliationSubjectEntity>().SingleAsync()).AcknowledgedVersion.ShouldBe(0);
        (await db.Set<MediaReconciliationRequestEntity>().CountAsync()).ShouldBe(2);
        (await execution.ReadOutcomeAsync(claim.Job.Id)).ShouldBeNull();
        (await db.Jobs.CountAsync()).ShouldBe(1);
    }
    [Fact]
    public async Task Execution_ObsoleteCapture_StopsAndReleasesEveryPinBeforeNoWorkSettlementWithoutInventingVerification()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var claim = await AcceptExecutionAsync(db, new MediaReconciliationSubject.Representation(graph.Cd.Id), false);
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        var execution = Execution(db, StoppedCustody(), PolicyDecision(TargetDecoder, true));
        var attempt = await execution.CaptureAsync(VerifyCapture(claim.Job.Id, 1, graph.Cd, TargetDecoder, 'b'));
        attempt.IsError.ShouldBeFalse();
        (await execution.ReadAttemptOutcomeAsync(claim.Job.Id, attempt.Value.Number)).ShouldBeNull();
        (await execution.FinishAsync(claim.Job.Id, 1, MediaReconciliationOutcomeKind.Completed, "no-work", true)).FirstError.ShouldBe(MediaReconciliationErrors.Changed);
        (await execution.SupersedeAttemptAsync(claim.Job.Id, "no-further-work", true)).IsError.ShouldBeFalse();
        var stopped = (await execution.ReadAttemptOutcomeAsync(claim.Job.Id, attempt.Value.Number)).ShouldNotBeNull();
        stopped.Fingerprint.ShouldBe(attempt.Value.Capture.Identity.Fingerprint);
        stopped.Disposition.ShouldBe(MediaReconciliationAttemptDisposition.Superseded);
        stopped.RepresentationId.ShouldBeNull();
        stopped.VerificationId.ShouldBeNull();
        stopped.CreationId.ShouldBeNull();
        stopped.InspectionId.ShouldBeNull();
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(0);
        (await new MediaFactReader(db).ReadAsync([graph.Cd.Id]))[graph.Cd.Id].Verifications.ShouldHaveSingleItem();
        (await execution.ReadDemandAsync(claim.Job.Id)).ShouldNotBeNull().Version.ShouldBe(1);
        (await execution.FinishAsync(claim.Job.Id, 1, MediaReconciliationOutcomeKind.Completed, "no-work", true)).IsError.ShouldBeFalse();
    }

    [Fact]
    public async Task Execution_WrongCapturedRequestVersion_CannotFailAnOtherwiseMatchingAttempt()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var claim = await AcceptExecutionAsync(db, new MediaReconciliationSubject.Representation(graph.Cd.Id), false);
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        var execution = Execution(db, StoppedCustody(), PolicyDecision(TargetDecoder, true));
        var captured = await execution.CaptureAsync(VerifyCapture(claim.Job.Id, 1, graph.Cd, TargetDecoder, 'b'));
        captured.IsError.ShouldBeFalse();
        var wrong = new MediaReconciliationAttempt(captured.Value.Number, VerifyCapture(claim.Job.Id, 2, graph.Cd, TargetDecoder, 'b'), null);
        wrong.Capture.Identity.Fingerprint.ShouldBe(captured.Value.Capture.Identity.Fingerprint);
        (await execution.FailAttemptAsync(wrong, "wrong-request", true, true)).FirstError.ShouldBe(MediaReconciliationErrors.Changed);
        (await execution.ReadAttemptOutcomeAsync(claim.Job.Id, captured.Value.Number)).ShouldBeNull();
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(1);
    }

}
