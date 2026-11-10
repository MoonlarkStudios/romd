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
using Romd.Persistence.MediaFacts;
using Romd.Persistence.Repositories;
using Romd.Persistence.Repositories.Reconciliation;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class CatalogCdLayoutBoundaryTests
{
    private static RomMediaComposition CatalogComposition(ConversionMode mode) =>
        RomMediaPolicyComposition.Create(RomMediaPolicyComposition.Registrations.Select(row =>
            row.Kind == "ps1-cd-set"
                ? row with { PlatformKeys = ["fixture"], ConversionMode = mode } : row).ToArray());
    private static readonly RomChdEncoderBuild CatalogEncoder = new("linux-x64", "mame0280", MediaFactGraphFixture.Hash('e'), 1);
    private static MediaReconciliationDecision CatalogDecision(RomMediaComposition composition, bool canEncode)
    {
        var capabilities = Substitute.For<IMediaHostCapabilities>();
        capabilities.CanDecode(Arg.Any<string>()).Returns(true);
        capabilities.CanEncode(Arg.Any<string>()).Returns(canEncode);
        capabilities.Decoder(RomRepresentationEncoding.Chd).Returns(Decoder);
        capabilities.ManagedDecoderBuild.Returns("fixture-managed");
        capabilities.Qualifies(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        capabilities.QualifiedRuntime(Arg.Any<string>()).Returns(Decoder.Rid);
        var profile = Substitute.For<IMediaReconciliationMediaProfile>();
        profile.LimitsFingerprint.Returns(MediaFactGraphFixture.Hash('b'));
        profile.OriginalInspectionDecoder.Returns(new RomDecoderIdentity.Managed("fixture-inspector"));
        profile.Encoder(Arg.Any<string>()).Returns(new MediaReconciliationEncoder(CatalogEncoder.BuildLabel, null));
        return new(composition.Kinds, composition.Reconciliation, capabilities, profile);
    }
    private MediaReconciliationSnapshotReader CatalogSnapshots(RomMediaComposition composition, IRomCdDescriptorIdentityReader descriptors) =>
        new(database.CreateOptions(), composition.Kinds, descriptors);
    private async Task<JobExecutionClaim<MediaReconciliationJob>> ClaimCatalogAsync(RomdDbContext db, bool conversion)
    {
        Guid job;
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var requested = await new MediaReconciliationRequests(db, TimeProvider.System).StageSubjectAsync(new(Guid.NewGuid(),
                new MediaReconciliationSubject.CatalogCdLayout(2), conversion, MediaReconciliationCause.Import, null, null));
            requested.IsError.ShouldBeFalse();
            job = requested.Value.Job.Id;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        return (await new ClaimedJobRepository<MediaReconciliationJob, MediaReconciliationJobRepository>(new(db, TimeProvider.System), db, TimeProvider.System, new())
            .TryClaimExecutionAsync(job, "catalog-source-fixture")).ShouldNotBeNull();
    }
    private MediaReconciliationExecution CatalogExecution(RomdDbContext db, RomMediaComposition composition, MediaReconciliationDecision decision)
    {
        var custody = Substitute.For<IMediaRunCustody>();
        custody.EnsureJobStoppedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Result.Success);
        return new(db, database.CreateOptions(), new EfUnitOfWork(db), custody, TimeProvider.System, composition.Kinds, decision);
    }

    [Theory]
    [InlineData(ConversionMode.OnRequest, false)]
    [InlineData(ConversionMode.OnRequest, true)]
    [InlineData(ConversionMode.Automatic, false)]
    [InlineData(ConversionMode.Automatic, true)]
    public async Task ImportDemand_CatalogGraph_UsesRegisteredConversionModeAndExplicitIntent(ConversionMode mode, bool conversion)
    {
        await RetainAsync(preferred: true, ObservedContainer(canonical: false));
        await using var db = database.CreateContext();
        var claim = await ClaimCatalogAsync(db, conversion);
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        var composition = CatalogComposition(mode);
        var decision = CatalogDecision(composition, canEncode: true);
        var execution = CatalogExecution(db, composition, decision);
        var demand = (await execution.ReadDemandAsync(claim.Job.Id)).ShouldNotBeNull();
        demand.ConversionRequested.ShouldBe(conversion);
        var facts = (await CatalogSnapshots(composition, Substitute.For<IRomCdDescriptorIdentityReader>()).ReadAsync(demand.Subject)).Value;

        var result = decision.Decide(claim.Job.Id, demand, facts);

        result.IsError.ShouldBeFalse();
        if (mode == ConversionMode.Automatic || conversion)
            result.Value.ShouldBeOfType<MediaReconciliationDirective.Encode>();
        else
            result.Value.ShouldBeOfType<MediaReconciliationDirective.Settle>().Code
                .ShouldBe(MediaReconciliationNoWork.ConversionNotRequested.ToString());
        (await CountAsync(db, "MediaReconciliationAttempts")).ShouldBe(0);
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PolicyCutover_AutomaticToOnRequest_RevalidatesIntentAtCaptureAndPublication(bool conversion)
    {
        var (graph, binding, revision) = await RetainAsync(preferred: true, ObservedContainer(canonical: false));
        await using var db = database.CreateContext();
        var claim = await ClaimCatalogAsync(db, conversion);
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        var automatic = CatalogComposition(ConversionMode.Automatic);
        var previousDecision = CatalogDecision(automatic, canEncode: true);
        var previousExecution = CatalogExecution(db, automatic, previousDecision);
        var demand = (await previousExecution.ReadDemandAsync(claim.Job.Id)).ShouldNotBeNull();
        var facts = (await CatalogSnapshots(automatic, Substitute.For<IRomCdDescriptorIdentityReader>()).ReadAsync(demand.Subject)).Value;
        var oldCapture = previousDecision.Decide(claim.Job.Id, demand, facts).Value
            .ShouldBeOfType<MediaReconciliationDirective.Encode>().Capture;
        var onRequest = CatalogComposition(ConversionMode.OnRequest);
        var currentDecision = CatalogDecision(onRequest, canEncode: true);
        var currentExecution = CatalogExecution(db, onRequest, currentDecision);

        if (!conversion)
        {
            (await currentExecution.CaptureAsync(oldCapture)).FirstError.ShouldBe(MediaReconciliationErrors.Changed);
            (await CountAsync(db, "MediaReconciliationAttempts")).ShouldBe(0);
            (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(0);
        }
        var captured = await previousExecution.CaptureAsync(oldCapture);
        captured.IsError.ShouldBeFalse();
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(2);
        await using (var transaction = await new EfUnitOfWork(db).BeginTransactionAsync())
        {
            var prepared = await currentExecution.PrepareAsync(captured.Value, []);
            if (conversion)
                prepared.IsError.ShouldBeFalse();
            else
                prepared.FirstError.ShouldBe(MediaReconciliationErrors.Changed);
            await transaction.RollbackAsync();
        }
        (await currentExecution.ReadAttemptOutcomeAsync(claim.Job.Id, captured.Value.Number)).ShouldBeNull();
        (await currentExecution.SupersedeAttemptAsync(claim.Job.Id, "policy-replanned", true)).IsError.ShouldBeFalse();
        (await currentExecution.ReadAttemptOutcomeAsync(claim.Job.Id, captured.Value.Number)).ShouldNotBeNull()
            .Disposition.ShouldBe(MediaReconciliationAttemptDisposition.Superseded);
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(0);
        if (!conversion)
        {
            currentDecision.Decide(claim.Job.Id, demand, facts).Value.ShouldBeOfType<MediaReconciliationDirective.Settle>()
                .Code.ShouldBe(MediaReconciliationNoWork.ConversionNotRequested.ToString());
            (await currentExecution.FinishAsync(claim.Job.Id, demand.Version, MediaReconciliationOutcomeKind.Completed,
                MediaReconciliationNoWork.ConversionNotRequested.ToString(), true)).IsError.ShouldBeFalse();
        }
        var retained = (await new MediaFactReader(db).ReadAsync([graph.Id]))[graph.Id];
        retained.Outputs.ShouldBe(graph.Outputs);
        retained.Descriptors.ShouldBe(graph.Descriptors);
        retained.Verifications.Select(proof => new
        {
            proof.Id,
            proof.ArtifactSha256,
            proof.ArtifactSize,
            proof.Decoder,
            proof.CheckedProfile,
            proof.VerifierBuild,
            proof.ObservedAt,
            proof.ObservedParametersSha256,
            proof.ComputedRawSha1,
            proof.ComputedOverallSha1
        }).ShouldBe(graph.Verifications.Select(proof => new
        {
            proof.Id,
            proof.ArtifactSha256,
            proof.ArtifactSize,
            proof.Decoder,
            proof.CheckedProfile,
            proof.VerifierBuild,
            proof.ObservedAt,
            proof.ObservedParametersSha256,
            proof.ComputedRawSha1,
            proof.ComputedOverallSha1
        }));
        retained.Verifications.SelectMany(proof => proof.Outputs).ShouldBe(graph.Verifications.SelectMany(proof => proof.Outputs));
        await using (var read = await db.Database.BeginTransactionAsync())
        {
            var retainedBinding = (await CatalogCdLayoutRead.ReadRetainedAsync(db, 2, default)).ShouldNotBeNull();
            retainedBinding.Id.ShouldBe(binding);
            retainedBinding.Revision.ShouldBe(revision);
            await read.CommitAsync();
        }
        (await db.RomFiles.Select(row => row.PreferredOutputId).ToArrayAsync()).ShouldBe(graph.Outputs.Select(row => (int?)row.Id).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Snapshot_CatalogLayout_UsesActualMeasuredMembersWithoutAnOriginalLayoutOrCreation(bool canonical)
    {
        var (graph, id, revision) = await RetainAsync(preferred: true, ObservedContainer(canonical));
        var descriptorReader = Substitute.For<IRomCdDescriptorIdentityReader>();
        var result = await CatalogSnapshots(CatalogComposition(ConversionMode.Automatic), descriptorReader).ReadAsync(new MediaReconciliationSubject.CatalogCdLayout(2));
        result.IsError.ShouldBeFalse();
        var facts = result.Value.ShouldBeOfType<MediaReconciliationFacts.Layout>();
        var context = facts.Sources.Layout.ShouldBeOfType<MediaReconciliationLayout.Catalog>();
        context.Subject.ShouldBe(new MediaReconciliationSubject.CatalogCdLayout(2));
        context.BindingId.ShouldBe(id);
        context.Revision.ShouldBe(revision);
        context.Requirement.ShouldBe(Publication(graph.Id, revision).Requirement);
        facts.CurrentArchiveId.ShouldBe(graph.Id);
        facts.Archives.Single().Id.ShouldBe(graph.Id);
        facts.OwnershipNeedsAlignment.ShouldBeFalse();
        facts.Sources.Inputs.OfType<MediaReconciliationInput.Logical>().Single().Output.Id.ShouldBe(graph.Outputs.Single().Id);
        var cue = facts.Sources.Inputs.OfType<MediaReconciliationInput.Stored>().Single();
        cue.Identity.ShouldBe(Cd(Cue));
        cue.Identity.Sha1.ShouldNotBe(context.Requirement.Sha1);
        descriptorReader.ReceivedCalls().ShouldBeEmpty();
        await using var db = database.CreateContext();
        (await db.OriginalDiscLayouts.CountAsync()).ShouldBe(0);
        (await db.CdArchiveOwners.CountAsync()).ShouldBe(0);
        graph.Creations.ShouldBeEmpty();
        graph.CdBootInspections.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true, false, ConversionMode.OnRequest)]
    [InlineData(true, true, ConversionMode.OnRequest)]
    [InlineData(false, false, ConversionMode.OnRequest)]
    [InlineData(false, true, ConversionMode.OnRequest)]
    [InlineData(true, false, ConversionMode.Automatic)]
    [InlineData(true, true, ConversionMode.Automatic)]
    [InlineData(false, false, ConversionMode.Automatic)]
    [InlineData(false, true, ConversionMode.Automatic)]
    public async Task Plan_CatalogArtifact_ReusesCanonicalBytesAndUsesRegisteredModeForEncoding(bool canonical, bool conversion, ConversionMode mode)
    {
        await RetainAsync(preferred: true, ObservedContainer(canonical));
        var composition = CatalogComposition(mode);
        var subject = new MediaReconciliationSubject.CatalogCdLayout(2);
        var facts = (await CatalogSnapshots(composition, Substitute.For<IRomCdDescriptorIdentityReader>()).ReadAsync(subject)).Value;
        var planned = CatalogDecision(composition, canEncode: !canonical).Decide(Guid.NewGuid(), new(subject, 1, conversion, false), facts);
        planned.IsError.ShouldBeFalse();
        if (canonical)
            planned.Value.ShouldBeOfType<MediaReconciliationDirective.Settle>().Code.ShouldBe(MediaReconciliationNoWork.Canonical.ToString());
        else if (mode == ConversionMode.Automatic || conversion)
            planned.Value.ShouldBeOfType<MediaReconciliationDirective.Encode>().Capture.Sources.Layout.ShouldBeOfType<MediaReconciliationLayout.Catalog>();
        else
            planned.Value.ShouldBeOfType<MediaReconciliationDirective.Settle>().Code.ShouldBe(MediaReconciliationNoWork.ConversionNotRequested.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Capture_CatalogRevision_PinsTheCompleteGraphAndRejectsAnExactBindingReplacement(bool replaceBinding)
    {
        var (graph, id, revision) = await RetainAsync(preferred: true, ObservedContainer(canonical: false));
        await using var db = database.CreateContext();
        var claim = await ClaimCatalogAsync(db, conversion: true);
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        var execution = CatalogExecution(db, CatalogComposition(ConversionMode.Automatic), CatalogDecision(CatalogComposition(ConversionMode.Automatic), canEncode: true));
        var demand = (await execution.ReadDemandAsync(claim.Job.Id)).ShouldNotBeNull();
        var facts = (await CatalogSnapshots(CatalogComposition(ConversionMode.Automatic), Substitute.For<IRomCdDescriptorIdentityReader>()).ReadAsync(demand.Subject)).Value;
        var directive = CatalogDecision(CatalogComposition(ConversionMode.Automatic), canEncode: true).Decide(claim.Job.Id, demand, facts).Value.ShouldBeOfType<MediaReconciliationDirective.Encode>();
        var captured = await execution.CaptureAsync(directive.Capture);
        captured.IsError.ShouldBeFalse();
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(2);
        var row = await db.Database.SqlQuery<CatalogAttempt>($"""SELECT "OriginalLayoutId","CatalogFileId","CatalogBindingId","LayoutRevision" FROM romd."MediaReconciliationAttempts" WHERE "JobId"={claim.Job.Id}""").SingleAsync();
        row.OriginalLayoutId.ShouldBeNull();
        row.CatalogFileId.ShouldBe(2);
        row.CatalogBindingId.ShouldBe(id);
        row.LayoutRevision.ShouldBe(revision);
        if (replaceBinding)
        {
            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                var store = new CatalogCdLayoutStore(db);
                (await store.WithdrawAsync(id, revision, default)).ShouldBeTrue();
                (await store.PublishAsync(Publication(graph.Id, Guid.NewGuid()), default)).IsError.ShouldBeFalse();
                await transaction.CommitAsync();
            }
            await using var publication = await new EfUnitOfWork(db).BeginTransactionAsync();
            (await execution.PrepareAsync(captured.Value, [])).FirstError.ShouldBe(MediaReconciliationErrors.Changed);
            (await execution.ReadAttemptOutcomeAsync(claim.Job.Id, captured.Value.Number)).ShouldBeNull();
            (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(2);
            await publication.RollbackAsync();
        }
        (await CountAsync(db, "Verifications")).ShouldBe(1);
        (await db.CdArchiveOwners.CountAsync()).ShouldBe(0);
        (await db.OriginalDiscLayouts.CountAsync()).ShouldBe(0);
    }
    [Fact]
    public async Task Publish_DatExactCueWithoutOrdinaryOriginal_ReturnsSourceChangedWithoutAbortingTheCaller()
    {
        await using var db = database.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var graph = await GraphAsync(db, Decoder, preferred: true, ObservedContainer(canonical: false));
        await db.CatalogReleaseFiles.Where(row => row.Id == 2).ExecuteUpdateAsync(set => set
            .SetProperty(row => row.Size, Cue.Size).SetProperty(row => row.Sha1, Cue.Sha1)
            .SetProperty(row => row.Md5, Cue.Md5).SetProperty(row => row.Crc, Cue.Crc32));
        var publication = Publication(graph.Id, Guid.NewGuid());
        publication = publication with { Requirement = publication.Requirement with { Size = Cue.Size, Sha1 = Cue.Sha1, Md5 = Cue.Md5, Crc32 = Cue.Crc32 } };

        var result = await new CatalogCdLayoutStore(db).PublishAsync(publication, default);

        result.IsError.ShouldBeTrue();
        result.FirstError.Code.ShouldBe("CatalogCdLayout.SourceChanged");
        result.FirstError.Type.ShouldBe(ErrorType.Conflict);
        db.Database.CurrentTransaction.ShouldBeSameAs(transaction);
        (await CountAsync(db, "CatalogCdLayoutBindings")).ShouldBe(0);
        (await MediaFactLockSet.ReadAsync(db, [graph.Id], default))[graph.Id].Verifications.ShouldHaveSingleItem();
        await transaction.CommitAsync();
        await using var observer = database.CreateContext();
        (await CountAsync(observer, "Roms")).ShouldBe(1);
        (await CountAsync(observer, "CatalogCdLayoutBindings")).ShouldBe(0);
        (await new MediaFactReader(observer).ReadAsync([graph.Id]))[graph.Id].Descriptors.Single().Sha256.ShouldBe(Cue.Sha256);
    }

    private sealed record CatalogAttempt(int? OriginalLayoutId, int? CatalogFileId, int? CatalogBindingId, Guid LayoutRevision);
}
