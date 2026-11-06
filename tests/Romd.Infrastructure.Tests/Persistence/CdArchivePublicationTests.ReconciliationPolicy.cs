using Romd.Admin.Application.Source.Rom;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Romd.Application.Common.Content;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Domain.Hashing;
using Romd.Persistence.Repositories.Reconciliation;
using Romd.Domain.Source.Rom;
using Romd.Infrastructure.Storage;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class CdArchivePublicationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reconciliation_PublishedCdArchive_PlansDecoderWorkWithoutChangingOwnedFacts(bool withdrawLogicalOwners)
    {
        await Publish();
        await using var db = database.CreateContext();
        if (withdrawLogicalOwners)
            (await db.RomFiles.ExecuteDeleteAsync()).ShouldBe(2);
        var archive = await ArchiveAsync(db);
        var oldProof = archive.Verifications.Single();
        var outputIds = archive.Outputs.Select(output => output.Id).ToArray();
        var creation = archive.Creations.Single();
        var boot = archive.CdBootInspections.Single();
        var policy = RomMediaPolicyComposition.Create().Reconciliation[Recipe.Kind];
        var capabilities = Substitute.For<IMediaHostCapabilities>();
        capabilities.CanDecode(Recipe.Kind).Returns(true);
        capabilities.CanEncode(Recipe.Kind).Returns(false);
        capabilities.Qualifies(Recipe.Kind, Decoder.Rid).Returns(true);
        capabilities.Decoder(RomRepresentationEncoding.Chd).Returns(Upgraded(Decoder));

        var plan = MediaReconciliationPlanner.Decide(policy, new([archive], null, false, false), false, capabilities);

        var verify = plan.ShouldBeOfType<MediaReconciliationPlan.Verify>();
        verify.RepresentationId.ShouldBe(archive.Id);
        verify.Decoder.ShouldBe(new RomDecoderIdentity.Native(Upgraded(Decoder)));
        capabilities.Decoder(RomRepresentationEncoding.Chd).Returns(Decoder);
        MediaReconciliationPlanner.Decide(policy, new([archive], null, false, false), false, capabilities)
            .ShouldBeOfType<MediaReconciliationPlan.None>().Reason.ShouldBe(MediaReconciliationNoWork.Canonical);
        var retained = await ArchiveAsync(db);
        retained.Verifications.ShouldHaveSingleItem().Id.ShouldBe(oldProof.Id);
        retained.Verifications.Single().Outputs.Count.ShouldBe(2);
        retained.Outputs.Select(output => output.Id).ShouldBe(outputIds);
        retained.Outputs.ShouldAllBe(output => withdrawLogicalOwners ? output.RomId == null : output.RomId != null);
        retained.Creations.Single().Id.ShouldBe(creation.Id);
        retained.CdBootInspections.Single().Id.ShouldBe(boot.Id);
        (await db.CdArchiveOwners.CountAsync()).ShouldBe(1);
        (await db.Set<Romd.Persistence.Entities.MediaReconciliationPinEntity>().CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Reconciliation_SharedBinCanonicalArchives_DoNotCompeteForEachOthersPreferredBacking()
    {
        await SeedSecondReleaseAsync();
        await Publish();
        await PublishLayout(2, SecondMeasured(), 10, new() { { "b.cue", 5 } }, new() { { "b-track.bin", 4 }, { "audio.bin", 2 } });
        var composition = RomMediaPolicyComposition.Create();
        var descriptors = Substitute.For<IRomCdDescriptorIdentityReader>();
        var firstCue = Identity(originals["disc.cue"]);
        var otherCue = Identity(secondCue);
        descriptors.ReadAsync(3, firstCue.Sha256, firstCue.Size, Arg.Any<CancellationToken>()).Returns(firstCue);
        descriptors.ReadAsync(5, otherCue.Sha256, otherCue.Size, Arg.Any<CancellationToken>()).Returns(otherCue);
        var snapshots = new MediaReconciliationSnapshotReader(database.CreateOptions(), composition.Kinds, descriptors);
        var caps = Substitute.For<IMediaHostCapabilities>();
        caps.CanDecode(Arg.Any<string>()).Returns(true);
        caps.CanEncode(Arg.Any<string>()).Returns(false);
        caps.Qualifies(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        caps.Decoder(RomRepresentationEncoding.Chd).Returns(Decoder);
        caps.ManagedDecoderBuild.Returns("managed");
        var profile = Substitute.For<IMediaReconciliationMediaProfile>();
        profile.LimitsFingerprint.Returns(Sha256.Parse(new string('a', 64)));
        var planner = new MediaReconciliationPlanning(snapshots, new(composition.Kinds, composition.Reconciliation, caps, profile));
        foreach (int layoutId in new[] { 1, 2 })
        {
            var facts = (await snapshots.ReadAsync(new MediaReconciliationSubject.OriginalLayout(layoutId))).Value.ShouldBeOfType<MediaReconciliationFacts.Layout>();
            facts.OwnershipNeedsAlignment.ShouldBeFalse();
            var step = await planner.NextAsync(Guid.NewGuid(), new(new MediaReconciliationSubject.OriginalLayout(layoutId), 1, false, false));
            step.IsError.ShouldBeFalse();
            step.Value.ShouldBeOfType<MediaReconciliationDirective.Settle>().Code.ShouldBe(MediaReconciliationNoWork.Canonical.ToString());
        }
    }
}
