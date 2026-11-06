using Romd.Admin.Application.Source.Rom;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NSubstitute;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Domain.Source.Rom;
using Romd.Application.Common.Content;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Storage;
using Romd.Persistence.Entities;
using Romd.Persistence.MediaFacts;
using Romd.Persistence.Repositories.Reconciliation;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class MediaReconciliationSchemaTests
{
    private static async Task AddLayoutCatalogAsync(Romd.Persistence.RomdDbContext db, MediaFactGraphFixture graph)
    {
        var identity = graph.Source.Outputs.Single().Identity;
        db.CatalogReleaseFiles.Add(new() { CatalogReleaseId = 1, FileFingerprint = "original-bin", Name = "catalog.bin", Size = identity.Size, Sha1 = identity.Sha1 });
        // A reference descriptor is not this layout's retained original CUE.
        db.CatalogReleaseFiles.Add(new() { CatalogReleaseId = 1, FileFingerprint = "reference-cue", Name = "catalog.cue", Size = 17, Sha1 = MediaFactGraphFixture.Short('b') });
        await db.SaveChangesAsync();
    }
    private MediaReconciliationSnapshotReader SnapshotReader(MediaFactGraphFixture graph)
    {
        var descriptors = Substitute.For<IRomCdDescriptorIdentityReader>();
        var cue = ((RomReconstructionManifest.CdTracks)graph.Cd.Manifest).Members.OfType<RomCdStoredMember>().Single().Identity;
        descriptors.ReadAsync(3, cue.Sha256, cue.Size, Arg.Any<CancellationToken>()).Returns(cue);
        return new(_database.CreateOptions(), RomMediaPolicyComposition.Create().Kinds, descriptors);
    }

    [Fact]
    public async Task Snapshot_CompleteLayout_UsesActualOriginalIdentitiesAndUniqueCurrentCatalogBinding()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var expected = await LayoutSourcesAsync(db, graph);
        var result = await SnapshotReader(graph).ReadAsync(new MediaReconciliationSubject.OriginalLayout(1));
        result.IsError.ShouldBeFalse();
        var layout = result.Value.ShouldBeOfType<MediaReconciliationFacts.Layout>();
        layout.KindId.ShouldBe("ps1-cd-set");
        var actualContext = layout.Sources.Layout.ShouldBeOfType<MediaReconciliationLayout.Original>();
        var expectedContext = expected.Layout.ShouldBeOfType<MediaReconciliationLayout.Original>();
        (actualContext.LayoutId, actualContext.Subject, actualContext.Revision, actualContext.PlatformId, actualContext.ReleaseId, actualContext.CatalogFingerprint, actualContext.EntryPoint)
            .ShouldBe((expectedContext.LayoutId, expectedContext.Subject, expectedContext.Revision, expectedContext.PlatformId, expectedContext.ReleaseId, expectedContext.CatalogFingerprint, expectedContext.EntryPoint));
        layout.Sources.Inputs.OfType<MediaReconciliationInput.Logical>().Single().Output.Id.ShouldBe(graph.Source.Outputs.Single().Id);
        layout.Sources.Inputs.OfType<MediaReconciliationInput.Stored>().Single().FileId.ShouldBe(3);
        layout.Sources.Fingerprint.ShouldBe(expected.Fingerprint);
        layout.Archives.ShouldBeEmpty();
        layout.CurrentArchiveId.ShouldBeNull();
        layout.OwnershipNeedsAlignment.ShouldBeFalse();
    }

    [Fact]
    public async Task Snapshot_AmbiguousCurrentCatalog_DoesNotAuthorizeEncodingUnderTheEarlierMatch()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        await LayoutSourcesAsync(db, graph);
        db.CatalogReleases.Add(new() { Id = 2, PlatformId = 1, CatalogTitleId = 1, Fingerprint = "second-candidate", Name = "Other" });
        db.CatalogReleaseFiles.Add(new() { CatalogReleaseId = 2, FileFingerprint = "other-bin", Name = "other.bin", Size = graph.Source.Outputs.Single().Identity.Size, Sha1 = graph.Source.Outputs.Single().Identity.Sha1 });
        await db.SaveChangesAsync();
        var result = await SnapshotReader(graph).ReadAsync(new MediaReconciliationSubject.OriginalLayout(1));
        result.IsError.ShouldBeFalse();
        result.Value.ShouldBeOfType<MediaReconciliationFacts.None>().Code.ShouldBe("layout-catalog-ambiguous");
        (await new MediaFactReader(db).ReadAsync([graph.Cd.Id]))[graph.Cd.Id].Verifications.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Snapshot_ReconstructedBinBacking_IsASourceRatherThanThisLayoutsArchiveOwner()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        await LayoutSourcesAsync(db, graph);
        await db.OriginalDiscLayoutFiles.ExecuteUpdateAsync(set => set.SetProperty(row => row.FileId, graph.Cd.FileId));
        var result = await SnapshotReader(graph).ReadAsync(new MediaReconciliationSubject.OriginalLayout(1));
        result.IsError.ShouldBeFalse();
        var layout = result.Value.ShouldBeOfType<MediaReconciliationFacts.Layout>();
        var source = layout.Sources.Inputs.OfType<MediaReconciliationInput.Logical>().Single();
        source.Graph.Id.ShouldBe(graph.Cd.Id);
        source.Output.Id.ShouldBe(graph.Cd.Outputs.Single().Id);
        layout.CurrentArchiveId.ShouldBeNull();
        layout.Archives.ShouldBeEmpty();
    }

    [Fact]
    public async Task Ownership_OriginalLayoutDependency_PreservesCompleteReconstructionAndEveryBuildAfterLogicalWithdrawal()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        await LayoutSourcesAsync(db, graph);
        await db.OriginalDiscLayoutFiles.ExecuteUpdateAsync(set => set.SetProperty(row => row.FileId, graph.Cd.FileId));
        var proof = Measurement(graph.Cd, TargetDecoder);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            (await new MediaFactPublisher(db).AppendVerificationAsync(graph.Cd.Id, proof)).ShouldBeTrue();
            await transaction.CommitAsync();
        }
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM romd.\"Roms\" WHERE \"Id\"={graph.RomId}");
        (await new MediaRepresentationCleanup(db).CollectAsync(256)).ShouldBe(1); // the unrelated identity graph has no remaining owner
        var retained = (await new MediaFactReader(db).ReadAsync([graph.Cd.Id]))[graph.Cd.Id];
        retained.Outputs.Single().RomId.ShouldBeNull();
        retained.Verifications.Select(row => row.Id).ShouldBe([graph.Cd.Verifications.Single().Id, proof.Id], ignoreOrder: true);
        retained.Descriptors.Single().FileId.ShouldBe(3);
        var snapshot = await SnapshotReader(graph).ReadAsync(new MediaReconciliationSubject.Representation(graph.Cd.Id));
        snapshot.IsError.ShouldBeFalse();
        snapshot.Value.ShouldBeOfType<MediaReconciliationFacts.Representation>().Graph.Id.ShouldBe(graph.Cd.Id);
        await RejectAsync(db, $"DELETE FROM romd.\"Representations\" WHERE \"Id\"={graph.Cd.Id}", atCommit: false);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var request = await new MediaReconciliationRequests(db, TimeProvider.System).StageSubjectAsync(
                new(Guid.NewGuid(), new MediaReconciliationSubject.Representation(graph.Cd.Id), false, MediaReconciliationCause.Readiness, null, null));
            request.IsError.ShouldBeFalse();
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        await db.OriginalDiscLayouts.ExecuteDeleteAsync();
        (await new MediaRepresentationCleanup(db).CollectAsync(256)).ShouldBe(1);
        (await new MediaFactReader(db).ReadAsync([graph.Cd.Id])).ShouldBeEmpty();
        (await db.Database.SqlQuery<int>($"SELECT count(*)::integer AS \"Value\" FROM romd.\"Verifications\" WHERE \"RepresentationId\"={graph.Cd.Id}").SingleAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData("file")]
    [InlineData("rom")]
    public async Task Ownership_NewLayoutDependency_CannotPointAtUnmeasuredOrDifferentLogicalBytes(string change)
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        await LayoutSourcesAsync(db, graph);
        var error = await Should.ThrowAsync<PostgresException>(() => change == "file"
            ? db.OriginalDiscLayoutFiles.ExecuteUpdateAsync(set => set.SetProperty(row => row.FileId, 3))
            : db.OriginalDiscLayoutFiles.ExecuteUpdateAsync(set => set.SetProperty(row => row.RomId, 999)));
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        var member = await db.OriginalDiscLayoutFiles.SingleAsync();
        member.FileId.ShouldBe(graph.Source.FileId);
        member.RomId.ShouldBe(graph.RomId);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Snapshot_OwnedArchiveSidecars_RemainExactCapturedDependenciesWhenAssociationMetadataNeedsAlignment(bool staleOwnerRevision)
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var layout = await LayoutSourcesAsync(db, graph);
        var extra = Romd.PostgreSql.TestSupport.MediaFactFixtures.Identity([0x53, 0x42, 0x49, 0, 0xfe]);
        db.Files.Add(new() { Id = 4, Sha256 = extra.Sha256, Size = extra.Size, SizeOnDisk = extra.Size });
        await db.SaveChangesAsync();
        var original = (RomReconstructionManifest.CdTracks)graph.Cd.Manifest;
        var manifest = new RomReconstructionManifest.CdTracks(original.LogicalFrames, original.Tracks,
            [.. original.Members, new RomCdStoredMember("disc.sbi", new(extra.Size, extra.Sha256, extra.Sha1, extra.Md5, extra.Crc32), RomCdStoredRole.Sidecar)]);
        var prior = graph.Cd.Verifications.Single();
        var proof = new RomRepresentationVerification(Guid.NewGuid(), prior.ArtifactSha256, prior.ArtifactSize, prior.Decoder, prior.CheckedProfile,
            prior.VerifierBuild, prior.ObservedAt, prior.Outputs, graph.Cd.Container, prior.ComputedRawSha1, prior.ComputedOverallSha1);
        RomRepresentationFacts archive;
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            archive = await new MediaFactPublisher(db).PublishAsync(graph.Cd.FileId, graph.Cd.Container, manifest, proof,
                [new(new RomOutputSelector.Member(0), graph.RomId, false)], [.. graph.Cd.Descriptors, new(2, 4, extra.Sha256, extra.Size)]);
            var writer = new MediaCreationFactWriter(db);
            Guid boot = await writer.StageCdBootAsync(archive.Id, proof.Id, "fixture-inspector", prior.ObservedAt, MediaFactGraphFixture.Boot, default);
            var encoder = new RomChdEncoderBuild("linux-arm64", "mame0280", MediaFactGraphFixture.Hash('e'), 1);
            var creation = new RomRepresentationCreation(Guid.NewGuid(), proof.Id, RecipeId.Parse("fixture-recipe/v1"), encoder.BuildLabel, prior.ObservedAt.AddTicks(1),
                archive.Container.Fingerprint!.Value, encoder, [new(archive.Outputs.Single().Id,graph.Source.Id,graph.Source.Outputs.Single().Id,
                    graph.Source.Verifications.Single().Id,graph.Source.FileId,graph.Source.ArtifactSha256,graph.Source.ArtifactSize)], new(boot, MediaFactGraphFixture.Hash('e'), 100));
            await writer.StageCreationAsync(archive.Id, creation, default);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO romd."CdArchiveOwners" ("LayoutId","RepresentationId","VerificationId","PublicationId","LayoutRevision","PlatformId","ReleaseId","CatalogFingerprint","EntryPoint","PublishedAtUtcTicks")
                VALUES (1,{archive.Id},{proof.Id},{Guid.NewGuid()},{layout.Layout!.Revision},1,1,'fixture-layout','disc.cue',{DateTimeOffset.UtcNow.UtcTicks})
                """);
            await transaction.CommitAsync();
        }
        if (staleOwnerRevision)
            await db.OriginalDiscLayouts.ExecuteUpdateAsync(set => set.SetProperty(row => row.Revision, Guid.NewGuid()));
        var snapshots = SnapshotReader(graph);
        var loaded = await snapshots.ReadAsync(new MediaReconciliationSubject.OriginalLayout(1));
        loaded.IsError.ShouldBeFalse();
        var facts = loaded.Value.ShouldBeOfType<MediaReconciliationFacts.Layout>();
        var sidecar = facts.Sources.Inputs.OfType<MediaReconciliationInput.Stored>().Single(input => input.Role == MediaReconciliationSourceRole.Sidecar);
        sidecar.FileId.ShouldBe(4);
        sidecar.Identity.Sha256.ShouldBe(extra.Sha256);
        facts.CurrentArchiveId.ShouldBe(archive.Id);
        facts.OwnershipNeedsAlignment.ShouldBe(staleOwnerRevision);
        facts.Sources.Inputs.OfType<MediaReconciliationInput.Artifact>().Single().Graph.Id.ShouldBe(archive.Id);
        var claim = await AcceptExecutionAsync(db, new MediaReconciliationSubject.OriginalLayout(1), true);
        Guid actor = await AddActorAsync(db);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var accepted = await new MediaReconciliationRequests(db, TimeProvider.System).StageSubjectAsync(new(Guid.NewGuid(),
                new MediaReconciliationSubject.OriginalLayout(1), true, MediaReconciliationCause.Admin, actor, null));
            accepted.IsError.ShouldBeFalse();
            accepted.Value.Job.Id.ShouldBe(claim.Job.Id);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        var decoder = (RomDecoderIdentity.Native)prior.Decoder;
        var decision = PolicyDecision(decoder.Build, true);
        var planner = new MediaReconciliationPlanning(snapshots, decision);
        var execution = Execution(db, StoppedCustody(), decision);
        var demand = (await execution.ReadDemandAsync(claim.Job.Id)).ShouldNotBeNull();
        demand.ConversionRequested.ShouldBeTrue();
        var planned = await planner.NextAsync(claim.Job.Id, demand);
        planned.IsError.ShouldBeFalse();
        var encode = planned.Value.ShouldBeOfType<MediaReconciliationDirective.Encode>();
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        var captured = await execution.CaptureAsync(encode.Capture);
        captured.IsError.ShouldBeFalse(captured.IsError ? captured.FirstError.ToString() : null);
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(4);
        (await db.Set<MediaReconciliationPinEntity>().SingleAsync(pin => pin.FileId == 4)).RepresentationId.ShouldBeNull();
        (await new MediaFactReader(db).ReadAsync([archive.Id]))[archive.Id].Descriptors.Single(descriptor => descriptor.FileId == 4).Sha256.ShouldBe(extra.Sha256);
    }

}
