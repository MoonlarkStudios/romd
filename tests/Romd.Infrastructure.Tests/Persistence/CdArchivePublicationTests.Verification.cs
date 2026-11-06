using Romd.Persistence.Queries;
using Romd.Application.Common.Content;
using Romd.PostgreSql.TestSupport;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Domain.Hashing;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Persistence.Entities;
using Romd.Persistence.MediaFacts;
using Romd.Persistence.Repositories;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class CdArchivePublicationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifyOnly_CompleteArchiveWithoutOriginalBins_RetainsEveryDecoderAndCreationWithOrWithoutLogicalOwners(bool withdrawLogicalOwners)
    {
        var publication = await Publish();
        await using var db = database.CreateContext();
        (await db.Files.Where(file => file.Id == 1 || file.Id == 2).ExecuteDeleteAsync()).ShouldBe(2);
        if (withdrawLogicalOwners)
            (await db.RomFiles.ExecuteDeleteAsync()).ShouldBe(2);
        var before = await ArchiveAsync(db);
        var target = Upgraded(Decoder);
        var subject = new MediaReconciliationSubject.Representation(before.Id);
        var claim = await AcceptAndClaimAsync(db, subject, conversion: false);
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        var decision = Decision(target, canEncode: false, qualified: true);
        var execution = Execution(db, decision, stopped: true);
        var demand = (await execution.ReadDemandAsync(claim.Job.Id)).ShouldNotBeNull();
        var facts = (await Snapshots().ReadAsync(subject)).Value;
        var directive = decision.Decide(claim.Job.Id, demand, facts).Value.ShouldBeOfType<MediaReconciliationDirective.Verify>();
        var attempt = await execution.CaptureAsync(directive.Capture);
        attempt.IsError.ShouldBeFalse();
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(1);
        var measured = Remeasured(before, target);
        (await execution.RecordVerificationAsync(attempt.Value, measured, processTreeStopped: true)).IsError.ShouldBeFalse();
        // The worker replays the exact journal; a direct stale mutation cannot append another proof.
        (await execution.ReadAttemptOutcomeAsync(claim.Job.Id, attempt.Value.Number)).ShouldNotBeNull().VerificationId.ShouldBe(measured.Representation.Measurement.Id);
        (await execution.RecordVerificationAsync(attempt.Value, measured, processTreeStopped: true)).FirstError.ShouldBe(MediaReconciliationErrors.Changed);
        (await execution.FinishAsync(claim.Job.Id, demand.Version, MediaReconciliationOutcomeKind.Completed, "verified", processTreeStopped: true)).IsError.ShouldBeFalse();
        var after = await ArchiveAsync(db);
        after.Id.ShouldBe(before.Id);
        after.FileId.ShouldBe(before.FileId);
        after.Container.Fingerprint.ShouldBe(before.Container.Fingerprint);
        after.Outputs.ShouldBe(before.Outputs);
        after.Descriptors.ShouldBe(before.Descriptors);
        after.Verifications.Select(proof => proof.Decoder).ShouldBe([new RomDecoderIdentity.Native(Decoder), new RomDecoderIdentity.Native(target)], ignoreOrder: true);
        after.Verifications.Single(proof => proof.Id == publication.VerificationId).Outputs.ShouldBe(before.Verifications.Single().Outputs);
        after.Verifications.Single(proof => proof.Id == measured.Representation.Measurement.Id).Outputs.Count.ShouldBe(2);
        after.Creations.ShouldHaveSingleItem().Id.ShouldBe(publication.CreationId);
        after.Creations.Single().Sources.ShouldBe(before.Creations.Single().Sources);
        after.Creations.Single().VerificationId.ShouldBe(publication.VerificationId);
        after.Creations.Single().NativeEncoder.ShouldBe(before.Creations.Single().NativeEncoder);
        after.Creations.Single().ParametersSha256.ShouldBe(before.Creations.Single().ParametersSha256);
        after.CdBootInspections.ShouldContain(boot => boot.Id == before.CdBootInspections.Single().Id && boot.Boot == before.CdBootInspections.Single().Boot);
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(0);
        (await db.CdArchiveOwners.SingleAsync()).PublicationId.ShouldBe(publication.PublicationId);
        (await db.RomRepresentations.CountAsync()).ShouldBe(1);
        if (!withdrawLogicalOwners)
        {
            var member = (await Romd.PostgreSql.TestSupport.MediaTestServices.CreateContentCatalog(db).FindByRomIdAsync(2))!
                .Reconstruction.ShouldBeOfType<Romd.Application.Common.Content.RomReconstruction.CdMember>();
            member.HasDecoder(Decoder).ShouldBeTrue();
            member.HasDecoder(target).ShouldBeTrue();
        }
    }

    [Fact]
    public async Task SharedBin_SecondArchiveRetainsForeignPreferredBackingAndBothLayoutsSettleWithoutReencoding()
    {
        await SeedSecondReleaseAsync();
        var first = await Publish();
        await using (var db = database.CreateContext())
        {
            var snapshot = (await Snapshots().ReadAsync(new MediaReconciliationSubject.OriginalLayout(2))).Value.ShouldBeOfType<MediaReconciliationFacts.Layout>();
            snapshot.Sources.Inputs.OfType<MediaReconciliationInput.Logical>().Single(input => input.Path == "audio.bin").Graph.Id.ShouldBe(first.Graph.Id);
            (await db.OriginalDiscLayoutFiles.SingleAsync(row => row.OriginalDiscLayoutId == 2 && row.Path == "audio.bin")).FileId.ShouldBe(9);
        }
        var second = await PublishLayout(2, SecondMeasured(), 10, new() { ["b.cue"] = 5 }, new() { ["b-track.bin"] = 4, ["audio.bin"] = 2 });
        await using var observed = database.CreateContext();
        (await observed.RomArtifacts().SingleAsync(row => row.RomId == 2 && row.IsPreferred)).FileId.ShouldBe(9);
        (await observed.CdArchiveOwners.CountAsync()).ShouldBe(2);
        (await new FileRepository(observed).IsReferencedAsync(9, default)).ShouldBeTrue();
        (await new FileRepository(observed).IsReferencedAsync(10, default)).ShouldBeTrue();
        foreach (int layoutId in new[] { 1, 2 })
        {
            var subject = new MediaReconciliationSubject.OriginalLayout(layoutId);
            var facts = (await Snapshots().ReadAsync(subject)).Value.ShouldBeOfType<MediaReconciliationFacts.Layout>();
            facts.OwnershipNeedsAlignment.ShouldBeFalse();
            Decision(Decoder, canEncode: true, qualified: true).Decide(Guid.NewGuid(), new(subject, 1, true, false), facts).Value
                .ShouldBeOfType<MediaReconciliationDirective.Settle>().Code.ShouldBe(MediaReconciliationNoWork.Canonical.ToString());
        }
        (await ArchiveAsync(observed, 1)).Creations.Single().Id.ShouldBe(first.CreationId);
        (await ArchiveAsync(observed, 2)).Creations.Single().Id.ShouldBe(second.CreationId);
    }

    private static RomMediaVerificationMeasurement.Cd Remeasured(RomRepresentationFacts original, RomChdDecoderBuild decoder,
        IReadOnlyList<RomCdFileMember>? members = null, RomPs1CdBoot? boot = null)
    {
        var prior = original.Verifications[0];
        var layout = (RomReconstructionManifest.CdTracks)original.Manifest;
        var manifest = members is null ? layout : new RomReconstructionManifest.CdTracks(layout.LogicalFrames, layout.Tracks, members);
        var proof = new RomRepresentationVerification(Guid.NewGuid(), original.ArtifactSha256, original.ArtifactSize, new RomDecoderIdentity.Native(decoder),
            prior.CheckedProfile, "fixture-native-verifier/new", prior.ObservedAt.AddDays(1), MeasuredOutputs(manifest), original.Container,
            prior.ComputedRawSha1, prior.ComputedOverallSha1);
        return new(new(original.Container, manifest, proof), CdInspector, boot ?? original.CdBootInspections.Single().Boot,
            new(original.ArtifactSha256, Md5.Parse(new string('d', 32)), Sha1.Parse(new string('e', 40)), original.ArtifactSize));
    }


}
