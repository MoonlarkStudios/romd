using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Domain.Source.Rom;
using Romd.Admin.Application.Storage.Files;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class MediaReconciliationSchemaTests
{
    [Fact]
    public async Task StorageCategories_ActiveLogicalPinKeepsCatalogAttributionAfterItsSourceOutputOwnershipMoves()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        db.Platforms.Add(new() { Id = 1, Name = "Test", ShortName = "test" });
        db.DatFiles.Add(new()
        {
            Id = 1,
            Source = new() { CatalogSource = new() { Id = 1, Kind = "Dat", Status = "Active" } },
            Name = "DAT",
            Description = "fixture",
            Type = "Redump",
            PlatformId = 1,
            OriginalFilename = "fixture.dat",
            FileId = 3,
            GameCount = 1
        });
        db.SourceEntries.Add(new() { Id = 1, CatalogSourceId = 1, EntryKey = "disc", Name = "disc", PlatformId = 1 });
        db.DatGames.Add(new() { Id = 1, DatFileId = 1, SourceEntryId = 1, Name = "disc" });
        var output = graph.Source.Outputs.Single();
        db.DatRoms.Add(new() { Id = 1, DatGameId = 1, Name = "disc.bin", Size = output.Identity.Size, Sha1 = output.Identity.Sha1, RomFileId = graph.RomId });
        await db.SaveChangesAsync();
        var claim = await AcceptExecutionAsync(db, new MediaReconciliationSubject.LogicalRom(graph.RomId), false);
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, TimeProvider.System);
        var sourceProof = graph.Source.Verifications.Single();
        var sources = new MediaReconciliationSources([new MediaReconciliationInput.Logical(MediaReconciliationSourceRole.Whole, "disc.bin", graph.Source, output.Id, sourceProof.Id)], null, [],
            MediaReconciliationReferenceFingerprint.Compute([]));
        var capture = new MediaReconciliationCapture(claim.Job.Id, 1, sources, MediaReconciliationOperation.Inspect, null, MediaFactGraphFixture.Hash('a'), MediaFactGraphFixture.Hash('b'), sourceProof.Decoder, null, null, null);
        var execution = Execution(db, StoppedCustody(), PolicyDecision(TargetDecoder, true));
        (await execution.CaptureAsync(capture)).IsError.ShouldBeFalse();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var repository = Romd.PostgreSql.TestSupport.MediaTestServices.CreateRomRepository(db);
            await repository.SetPreferredRepresentationStagedAsync(graph.RomId, graph.Cd.Outputs.Single().Id);
            await repository.ReleaseRepresentationsStagedAsync(graph.RomId, [output.Id]);
            await transaction.CommitAsync();
        }
        (await db.RepresentationOutputs.SingleAsync(row => row.Id == output.Id)).RomId.ShouldBeNull();
        (await db.Set<MediaReconciliationPinEntity>().SingleAsync()).OutputId.ShouldBe(output.Id);
        var files = new FileRepository(db);
        (await files.IsReferencedAsync(graph.Source.FileId, default)).ShouldBeTrue();
        var stats = await files.GetStorageStatsAsync(default);
        stats.Breakdown.Single(row => row.Category == StorageCategories.RomMatched).FileCount.ShouldBe(2);
        stats.Breakdown.Single(row => row.Category == StorageCategories.RomUnmatched).FileCount.ShouldBe(1);
        (await execution.SupersedeAttemptAsync(claim.Job.Id, "current-source-moved", processTreeStopped: true)).IsError.ShouldBeFalse();
        var released = await files.GetStorageStatsAsync(default);
        released.Breakdown.Single(row => row.Category == StorageCategories.RomMatched).FileCount.ShouldBe(1);
        released.Breakdown.Single(row => row.Category == StorageCategories.RomUnmatched).FileCount.ShouldBe(2);
    }

    [Fact]
    public async Task StorageCategories_OriginalCueAndLayoutMembers_AreOwnedUnmatchedMediaRatherThanOrphans()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        await LayoutSourcesAsync(db, graph);
        var files = new FileRepository(db);
        (await files.IsReferencedAsync(3, default)).ShouldBeTrue();
        (await files.GetUnreferencedFileIdsOlderThanAsync(DateTimeOffset.UtcNow.AddMinutes(1), default)).ShouldBeEmpty();
        var stats = await files.GetStorageStatsAsync(default);
        stats.Breakdown.Single(row => row.Category == StorageCategories.RomUnmatched).FileCount.ShouldBe(3);
        stats.Breakdown.Single(row => row.Category == StorageCategories.Unattributed).FileCount.ShouldBe(0);
    }
}
