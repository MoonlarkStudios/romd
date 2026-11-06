using Romd.Application.Common.Content;
using Romd.Admin.Application.Storage.Files;
using Romd.Persistence;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Persistence.Entities;
using Romd.Persistence.MediaFacts;
using Romd.Persistence.Repositories;
using Romd.Persistence.Repositories.Reconciliation;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class CatalogCdLayoutBoundaryTests
{
    [Fact]
    public async Task Select_CatalogScopes_UsesCapturedCatalogFileBoundsAndWalksSkippedNamespaces()
    {
        var (graph, _, _) = await RetainAsync(preferred: true, ObservedContainer(canonical: true));
        await using var db = database.CreateContext();
        var requests = new MediaReconciliationRequests(db, TimeProvider.System);
        Guid job;
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var accepted = await requests.StageSelectionAsync(Guid.NewGuid(), new MediaReconciliationFilter.KindSubjects(CatalogComposition(ConversionMode.Automatic).Kinds.Kinds.Single(row => row.ManifestType == Romd.Application.Common.Content.MediaManifestType.CdTracks).Kind, 1),
                1, false, MediaReconciliationCause.Readiness, null);
            accepted.IsError.ShouldBeFalse();
            job = accepted.Value.Job.Id;
            var bounds = accepted.Value.Job.Request.ShouldBeOfType<MediaReconciliationRequest.SelectionWork>().Bounds;
            bounds.ShouldBe(new(graph.Outputs.Single().RomId!.Value, 0, graph.Id, 2));
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        // A later, independently associated catalog entry cannot extend the accepted finite selection.
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await StageAdditionalCatalogBindingAsync(db, graph.Id);
            await transaction.CommitAsync();
        }
        var claim = (await new ClaimedJobRepository<MediaReconciliationJob, MediaReconciliationJobRepository>(new(db, TimeProvider.System), db, TimeProvider.System, new())
            .TryClaimExecutionAsync(job, "catalog-selection-fixture")).ShouldNotBeNull();
        using var fence = JobExecutionFenceScope.Enter(job, claim.FenceToken, TimeProvider.System);
        var custody = Substitute.For<IMediaRunCustody>();
        custody.EnsureJobStoppedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Result.Success);
        var selections = new MediaReconciliationSelections(db, database.CreateOptions(), new EfUnitOfWork(db), requests,
            CatalogSnapshots(CatalogComposition(ConversionMode.Automatic), Substitute.For<IRomCdDescriptorIdentityReader>()), CatalogComposition(ConversionMode.Automatic).Kinds, custody, TimeProvider.System);
        var advanced = await selections.AdvanceAsync(job, 64);
        advanced.IsError.ShouldBeFalse();
        var progress = advanced.Value.Progress;
        progress.EnumerationComplete.ShouldBeTrue();
        progress.Selected.ShouldBe(3);
        progress.Created.ShouldBe(1);
        progress.Adopted.ShouldBe(0);
        progress.Skipped.ShouldBe(2);
        progress.Cursor.ShouldBe(new MediaReconciliationSubject.CatalogCdLayout(2));
        var children = await db.Set<MediaReconciliationDefinitionEntity>().Where(row => row.OriginSelectionJobId == job).ToArrayAsync();
        children.ShouldHaveSingleItem().SubjectKind.ShouldBe("CatalogCdLayout");
        children.Single().SubjectId.ShouldBe(2);
        (await db.Set<MediaReconciliationSubjectEntity>().AnyAsync(row => row.SubjectKind == "CatalogCdLayout" && row.SubjectId == 17)).ShouldBeFalse();
    }
    private static async Task StageAdditionalCatalogBindingAsync(Romd.Persistence.RomdDbContext db, int representationId)
    {
        db.Database.CurrentTransaction.ShouldNotBeNull();
        await CatalogTopologyFence.AcquireAsync(db, default);
        db.CatalogReleases.Add(new() { Id = 2, PlatformId = 1, CatalogTitleId = 1, Fingerprint = "sha1:later", Name = "Later" });
        db.CatalogReleaseFiles.AddRange(
            new() { Id = 3, CatalogReleaseId = 2, FileFingerprint = "later-bin", Name = "track.bin", Size = Bin.Size, Sha1 = Bin.Sha1, Md5 = Bin.Md5, Crc = Bin.Crc32 },
            new() { Id = 17, CatalogReleaseId = 2, FileFingerprint = "later-cue", Name = "disc.cue", Size = ExpectedCue.Size, Sha1 = ExpectedCue.Sha1, Md5 = ExpectedCue.Md5, Crc = ExpectedCue.Crc32 });
        await db.SaveChangesAsync();
        var publication = Publication(representationId, Guid.NewGuid());
        publication = publication with { Requirement = publication.Requirement with { CatalogFileId = 17, ReleaseId = 2, ReleaseFingerprint = "sha1:later", FileFingerprint = "later-cue" } };
        (await new CatalogCdLayoutStore(db).PublishAsync(publication, default)).IsError.ShouldBeFalse();
    }
}
