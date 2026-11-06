using Romd.Persistence.Queries;
using NSubstitute;
using Romd.Application.Common.Content;
using Romd.Consumer.Application.Delivery;
using Romd.Infrastructure.Storage;
using Romd.PostgreSql.TestSupport;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Application.Common.Ids;
using Romd.Consumer.Application.Libraries;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Persistence.Catalog;
using Romd.Persistence.Entities;
using Romd.Persistence.MediaFacts;
using Romd.Persistence.Repositories;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class CdArchivePublicationTests
{
    [Fact]
    public async Task Publication_CompleteOutputsAndJournalAreAtomic_LayoutKeepsLogicalHashes_AndSourcesCanBeCollected()
    {
        var publication = await Publish();
        Guid jobId = publication.JobId;
        await using var db = database.CreateContext();
        db.ChangeTracker.Clear();
        var outcome = (await Execution(db, Decision(Decoder, canEncode: false, qualified: true), stopped: true).ReadOutcomeAsync(jobId)).ShouldNotBeNull();
        outcome.Kind.ShouldBe(MediaReconciliationOutcomeKind.Completed);
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(0);
        (await db.RomRepresentations.CountAsync()).ShouldBe(1);
        var catalog = Romd.PostgreSql.TestSupport.MediaTestServices.CreateContentCatalog(db);
        var content = (await catalog.FindByRomIdAsync(1)).ShouldNotBeNull();
        content.FileId.ShouldBe(9);
        content.ContentSha256.ShouldBe(Identity(originals["track.bin"]).Sha256);
        content.Reconstruction.ShouldBeOfType<RomReconstruction.CdMember>();
        var retained = await ArchiveAsync(db);
        retained.Outputs.Count.ShouldBe(2);
        retained.Verifications.Count.ShouldBe(1);
        retained.CdBootInspections.Single().InspectorBuild.ShouldBe(CdInspector);
        var creation = retained.Creations.Single();
        creation.Sources.Count.ShouldBe(2);
        creation.Sources.Select(source => source.VerificationId).Distinct().Count().ShouldBe(2);
        creation.Cd.ShouldNotBeNull().SourceRequestBytes.ShouldBe(100);
        (await db.CdArchiveOwners.SingleAsync()).PublicationId.ShouldBe(publication.PublicationId);
        var dto = (await new OriginalDiscLayoutService(db, new FileMutationLock(db)).GetAsync(1, default)).Value;
        dto.Dependencies.Single(x => x.Path == "track.bin").Sha256.ShouldBe(content.ContentSha256.ToString());
        var ledger = new FileRepository(db);
        (await ledger.IsReferencedAsync(1, default)).ShouldBeFalse();
        (await ledger.IsReferencedAsync(2, default)).ShouldBeFalse();
        (await ledger.IsReferencedAsync(9, default)).ShouldBeTrue();
        // Mutating one output to another logical member must fail before it can authorize a read.
        var output = await db.RepresentationOutputs.SingleAsync(row => row.RomId == 1);
        var other = await db.RepresentationOutputs.SingleAsync(row => row.RomId == 2);
        var error = await Should.ThrowAsync<PostgresException>(() => db.RepresentationOutputs.Where(row => row.Id == output.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.MemberOrdinal, other.MemberOrdinal)));
        error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        var current = (await Snapshots().ReadAsync(new MediaReconciliationSubject.OriginalLayout(1))).Value;
        Decision(Decoder, canEncode: false, qualified: true).Decide(Guid.NewGuid(), new(new MediaReconciliationSubject.OriginalLayout(1), 1, false, false), current)
            .Value.ShouldBeOfType<MediaReconciliationDirective.Settle>().Code.ShouldBe(MediaReconciliationNoWork.Canonical.ToString());
    }

    [Fact]
    public async Task Publication_FailureAfterJournalStaging_RollsBackEveryOwnerAndKeepsOriginalPins()
    {
        await Should.ThrowAsync<IOException>(() => Publish(false));
        await using var db = database.CreateContext();
        (await db.CdArchiveOwners.CountAsync()).ShouldBe(0);
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(3);
        (await db.Database.SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM romd.\"MediaReconciliationAttempts\" WHERE \"Outcome\" IS NOT NULL").SingleAsync()).ShouldBe(0);
        (await db.RomRepresentations.CountAsync()).ShouldBe(2);
        (await db.Verifications.CountAsync()).ShouldBe(2);
        var originals = (await new MediaFactReader(db).ReadAsync((await db.RomRepresentations.Select(row => row.Id).ToArrayAsync()))).Values;
        originals.ShouldAllBe(graph => graph.Creations.Count == 0 && graph.CdBootInspections.Count == 0);
        (await db.RomArtifacts().OrderBy(row => row.RomId).Select(row => row.FileId).ToArrayAsync()).ShouldBe([1, 2]);
        (await new FileRepository(db).IsReferencedAsync(9, default)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("member")]
    [InlineData("decoder")]
    [InlineData("library")]
    [InlineData("descriptor")]
    public async Task WholeContainerAccess_RechecksEveryBinCurrentDescriptorAndLibrary(string revoke)
    {
        await Publish();
        await using var db = database.CreateContext();
        var admission = Substitute.For<IRomChdDeliveryAdmission>();
        admission.AssessCd(Arg.Any<RomReconstruction.CdMember>()).Returns(Decoder);
        var repository = await ConsumerArchives(db, admission);
        (await repository.FindAsync(new(actor), 1)).ShouldBeOfType<ConsumerLibraryReadResult<RomCdArchiveDownload>.Found>();
        if (revoke == "member")
            await db.CatalogReleaseFiles.Where(row => row.Name == "catalog-audio.bin").ExecuteDeleteAsync();
        else if (revoke == "library")
            await db.Users.Where(row => row.Id == actor).ExecuteUpdateAsync(set => set.SetProperty(row => row.LibraryId, (int?)null));
        else if (revoke == "descriptor")
            await db.OriginalDiscLayouts.ExecuteUpdateAsync(set => set.SetProperty(row => row.DescriptorFileId, 9));
        else
            admission.AssessCd(Arg.Any<RomReconstruction.CdMember>()).Returns((RomChdDecoderBuild?)null);
        (await repository.FindAsync(new(actor), 1)).ShouldNotBeOfType<ConsumerLibraryReadResult<RomCdArchiveDownload>.Found>();
        if (revoke == "descriptor")
            (await new RomCdArchiveDownloadCatalog(db, database.CreateOptions(), RomMediaPolicyComposition.CreateDeliveryPolicy()).FindForLayoutAsync(1)).ShouldNotBeNull();
    }

    [Theory]
    [InlineData("confirm")]
    [InlineData("detach")]
    [InlineData("reject")]
    public async Task Publication_LaterDecision_KeepsArchiveAndCueRecoverable_AndConfirmBindsArchiveBacking(string action)
    {
        var published = await Publish();
        await using (var review = database.CreateContext())
        {
            // The confirm UI offers archived originals as the same exact logical candidates.
            var release = (await new OriginalDiscLayoutService(review, new FileMutationLock(review)).CandidatesAsync(1, null, null, 100, default)).Value.Items.Single();
            release.Files.Select(x => (x.RomId, x.Sha256)).ShouldBe([(IdCoder.Encode(1),Identity(originals["track.bin"]).Sha256.ToString()),
                (IdCoder.Encode(2),Identity(originals["audio.bin"]).Sha256.ToString())], ignoreOrder: true);
            release.Requirements.Where(x => x.Name.EndsWith(".bin")).ShouldAllBe(x => x.OriginalAvailable);
        }
        var decided = await Decide(1, action);
        await using var db = database.CreateContext();
        var target = (await new RomCdArchiveDownloadCatalog(db, database.CreateOptions(), RomMediaPolicyComposition.CreateDeliveryPolicy()).FindForLayoutAsync(1)).ShouldNotBeNull();
        var retained = await ArchiveAsync(db);
        target.Representation.Id.ShouldBe(retained.Id);
        target.PublicationId.ShouldBe(published.PublicationId);
        target.Representation.Verifications.Single().Id.ShouldBe(retained.Verifications.Single().Id);
        ((RomReconstructionManifest.CdTracks)target.Representation.Manifest).Members.OfType<RomCdStoredMember>().Single(x => x.Role == RomCdStoredRole.Cue).Identity.ShouldBe(Identity(originals["disc.cue"]));
        var consumer = await (await ConsumerArchives(db)).FindAsync(new(actor), 1);
        if (action == "confirm")
        {
            decided.State.ShouldBe("accepted");
            decided.Issue.ShouldBeNull();
            decided.Dependencies.Select(x => x.Sha256).ShouldBe([Identity(originals["track.bin"]).Sha256.ToString(), Identity(originals["audio.bin"]).Sha256.ToString()]);
            (await db.OriginalDiscLayoutFiles.Where(x => x.BindingRevision == decided.Revision).Select(x => x.FileId).Distinct().ToArrayAsync()).ShouldBe([9]);
            consumer.ShouldBeOfType<ConsumerLibraryReadResult<RomCdArchiveDownload>.Found>();
            var facts = (await Snapshots().ReadAsync(new MediaReconciliationSubject.OriginalLayout(1))).Value;
            // Confirmation changes the layout revision; align its owned metadata using the existing measured archive.
            var alignment = Decision(Decoder, canEncode: false, qualified: true).Decide(Guid.NewGuid(), new(new MediaReconciliationSubject.OriginalLayout(1), 1, false, false), facts)
                .Value.ShouldBeOfType<MediaReconciliationDirective.Release>();
            alignment.RepresentationId.ShouldBe(published.Graph.Id);
            alignment.ProofId.ShouldBe(published.VerificationId);
            alignment.Capture.Sources.Layout.ShouldNotBeNull().Revision.ShouldBe(decided.Revision);
            (await db.Jobs.CountAsync()).ShouldBe(2);
            var requests = await db.Set<MediaReconciliationRequestEntity>().Where(row => row.Cause == "Facts").ToArrayAsync();
            requests.ShouldHaveSingleItem();
            var demand = (await db.Set<MediaReconciliationDefinitionEntity>().SingleAsync(row => row.JobId == requests.Single().JobId));
            demand.SubjectKind.ShouldBe("OriginalLayout");
            demand.SubjectId.ShouldBe(1);
            demand.RequestMode.ShouldBe("Subject");
            (await db.Set<MediaReconciliationSubjectEntity>().SingleAsync(row => row.SubjectKind == "OriginalLayout" && row.SubjectId == 1))
                .ConversionRequested.ShouldBeFalse();
            (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(0);
            // A changed confirmed catalog cannot authorize fresh encoding or alter the retained publication.
            await db.CatalogReleases.ExecuteUpdateAsync(set => set.SetProperty(row => row.Fingerprint, "changed-catalog"));
            var changed = (await Snapshots().ReadAsync(new MediaReconciliationSubject.OriginalLayout(1))).Value;
            changed.ShouldBeOfType<MediaReconciliationFacts.None>();
            (await ArchiveAsync(db)).Creations.Single().Id.ShouldBe(published.CreationId);
        }
        else
        {
            consumer.ShouldNotBeOfType<ConsumerLibraryReadResult<RomCdArchiveDownload>.Found>();
            (await Snapshots().ReadAsync(new MediaReconciliationSubject.OriginalLayout(1))).Value.ShouldBeOfType<MediaReconciliationFacts.None>();
            (await ArchiveAsync(db)).Creations.Single().Id.ShouldBe(published.CreationId);
        }
    }

    [Fact]
    public async Task FindForLayout_ArchiveFilename_TakesTheCatalogsChdExtension()
    {
        await Publish();
        await using var db = database.CreateContext();
        var renamed = new RomDeliveryPolicy(new([new(RomDeliveryFormat.Original,"Original",null),new(RomDeliveryFormat.Rvz,"RVZ",".rvz"),
            new(RomDeliveryFormat.Chd,"CHD",".cdz")]), RomMediaPolicyComposition.Create().Kinds);
        (await new RomCdArchiveDownloadCatalog(db, database.CreateOptions(), RomMediaPolicyComposition.CreateDeliveryPolicy()).FindForLayoutAsync(1)).ShouldNotBeNull()
            .Filename.ShouldBe("disc.chd");
        (await new RomCdArchiveDownloadCatalog(db, database.CreateOptions(), renamed).FindForLayoutAsync(1)).ShouldNotBeNull().Filename.ShouldBe("disc.cdz");
    }

    [Fact]
    public async Task SharedBin_SecondReleaseUploadedAfterArchive_ReconcilesThroughArchiveBacking()
    {
        await Publish();
        var upload = UploadJob.Create("second-region", createdAt: DateTimeOffset.UtcNow);
        await SeedSecondReleaseAsync(upload.Id, bound: false);
        await using (var seed = database.CreateContext())
        {
            seed.Set<UploadJobEntity>().Add(UploadJobEntity.FromDomain(upload));
            seed.JobItems.AddRange(JobItemEntity.FromDomain(JobItem.ForRom(upload.Id, "b-track.bin", secondTrack.Length, RomIngestOutcome.Ingested, 4, [], 1, null, createdAt: DateTimeOffset.UtcNow).WithUploadIdentity()),
                JobItemEntity.FromDomain(JobItem.ForRom(upload.Id, "audio.bin", originals["audio.bin"].Length, RomIngestOutcome.Deduplicated, 2, [], 1, null, createdAt: DateTimeOffset.UtcNow).WithUploadIdentity()));
            await seed.SaveChangesAsync();
        }
        await using var db = database.CreateContext();
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await new OriginalDiscLayoutService(db, new FileMutationLock(db)).ReconcileStagedAsync(upload.Id, default);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        (await db.OriginalDiscLayoutFiles.Where(x => x.OriginalDiscLayoutId == 2).OrderBy(x => x.Path).Select(x => x.FileId).ToArrayAsync()).ShouldBe([9, 4]);
        (await new OriginalDiscLayoutService(db, new FileMutationLock(db)).GetAsync(2, default)).Value.Issue.ShouldBe("ReviewRequired");
    }

}
