using Romd.Infrastructure.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.Persistence.Repositories.Reconciliation;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class MediaReconciliationSchemaTests
{
    [Theory]
    [InlineData("LogicalRom")]
    [InlineData("Representation")]
    public async Task Repository_SubjectNamespaces_LoadThroughTypedAndUnifiedQueries(string kind)
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        MediaReconciliationSubject subject = kind == "LogicalRom" ? new MediaReconciliationSubject.LogicalRom(graph.RomId) : new MediaReconciliationSubject.Representation(graph.Cd.Id);
        var captured = await CaptureAsync(db, graph.Cd, subject, TargetDecoder, 1);
        var typed = new MediaReconciliationJobRepository(db, TimeProvider.System);
        var unified = new JobRepository(db, TimeProvider.System);
        var loaded = (await typed.GetByIdAsync(captured.JobId)).ShouldNotBeNull();
        loaded.Request.ShouldBeOfType<MediaReconciliationRequest.SubjectWork>().Subject.ShouldBe(subject);
        loaded.PhaseEnum.ShouldBe(MediaReconciliationPhase.Planning);
        loaded.Outcome.ShouldBeNull();
        loaded.SelectionProgress.ShouldBeNull();
        (await unified.GetByIdAsync(captured.JobId)).ShouldBeOfType<MediaReconciliationJob>().Id.ShouldBe(captured.JobId);
        (await unified.GetRecentAsync()).Single().ShouldBeOfType<MediaReconciliationJob>().Id.ShouldBe(captured.JobId);
        var filter = new JobListFilter(null, null, null, null, "all", null, null, null, null, 20, IncludeMediaConvergence: true);
        (await unified.ListAsync(filter)).Single().ShouldBeOfType<MediaReconciliationJob>().Id.ShouldBe(captured.JobId);
        (await unified.ListAsync(filter with { IncludeMediaConvergence = false })).ShouldBeEmpty();
    }

    [Fact]
    public async Task Repository_AddSelection_StagesWithoutFlushingAndReadsTypedCapturedScope()
    {
        var (db, _) = await SetupAsync();
        await using var owned = db;
        var request = new MediaReconciliationRequest.SelectionWork(new MediaReconciliationFilter.KindSubjects("ps1-cd-set", null), new(7, 9, 11, 0), 2, conversionRequested: false);
        var job = MediaReconciliationJob.Create(Guid.NewGuid(), request, "selection", originSelectionJobId: null, actorId: null, DateTimeOffset.UnixEpoch);
        var repository = new MediaReconciliationJobRepository(db, TimeProvider.System);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            db.Files.Add(new() { Id = 4, Sha256 = MediaFactGraphFixture.Hash('b'), Size = 5, SizeOnDisk = 5 });
            await repository.AddAsync(job);
            // The caller still owns every staged mutation and the transaction boundary.
            await using (var observer = _database.CreateContext())
            {
                (await observer.Jobs.CountAsync()).ShouldBe(0);
                (await observer.Files.CountAsync()).ShouldBe(3);
            }
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO romd."MediaReconciliationRequests" ("Id","JobId","ActorId","Cause","RequestedVersion","ConversionRequested","CreatedAtUtcTicks")
                VALUES ({Guid.NewGuid()},{job.Id},NULL,'Readiness',0,false,{DateTimeOffset.UnixEpoch.UtcTicks})
                """);
            await transaction.CommitAsync();
        }
        var loaded = (await repository.GetByIdAsync(job.Id)).ShouldNotBeNull();
        var selection = loaded.Request.ShouldBeOfType<MediaReconciliationRequest.SelectionWork>();
        selection.Filter.ShouldBeOfType<MediaReconciliationFilter.KindSubjects>().Kind.ShouldBe("ps1-cd-set");
        selection.Bounds.ShouldBe(new(7, 9, 11, 0));
        selection.Limit.ShouldBe(2);
        selection.ConversionRequested.ShouldBeFalse();
        loaded.SelectionProgress.ShouldBe(new(null, 0, 0, 0, 0, 0, 0, false));
        loaded.OriginSelectionJobId.ShouldBeNull();
        (await new JobRepository(db, TimeProvider.System).GetRecentAsync()).Single().ShouldBeOfType<MediaReconciliationJob>().Id.ShouldBe(job.Id);
    }

    [Fact]
    public async Task Repository_StaleActiveSnapshot_CannotOverwritePublicationJournalOrRestoreSourcePins()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var captured = await CaptureAsync(db, graph.Cd, new MediaReconciliationSubject.Representation(graph.Cd.Id), TargetDecoder, 1);
        var repository = new MediaReconciliationJobRepository(db, TimeProvider.System);
        var stale = (await repository.GetByIdAsync(captured.JobId)).ShouldNotBeNull();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await StageRecordedAsync(db, captured, Measurement(graph.Cd, TargetDecoder));
            await FinishSubjectAsync(db, captured, version: 1);
            await transaction.CommitAsync();
        }
        stale.SetPhase(MediaReconciliationPhase.Encoding);
        await repository.CommitCheckpointAsync(db, stale);
        var current = (await repository.GetByIdAsync(captured.JobId)).ShouldNotBeNull();
        current.PhaseEnum.ShouldBe(MediaReconciliationPhase.Completed);
        current.Outcome.ShouldBe(MediaReconciliationOutcomeKind.Completed);
        current.OutcomeCode.ShouldBe("verified");
        current.CompletedAt.ShouldBe(DateTimeOffset.UnixEpoch.AddTicks(10));
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(0);
        (await new Romd.Persistence.MediaFacts.MediaFactReader(db).ReadAsync([graph.Cd.Id]))[graph.Cd.Id].Verifications.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Repository_ConcurrentExecutionClaims_AdmitOneWinnerAndRetainTheCapturedAttempt()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var captured = await CaptureAsync(db, graph.Cd, new MediaReconciliationSubject.Representation(graph.Cd.Id), TargetDecoder, 1);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE romd.\"Jobs\" SET \"ExecutionLeaseExpiresAtUtc\"=0 WHERE \"Id\"={captured.JobId}");
        await using var first = _database.CreateContext();
        await using var second = _database.CreateContext();
        ClaimedJobRepository<MediaReconciliationJob, MediaReconciliationJobRepository> Claims(RomdDbContext context) =>
            new(new(context, TimeProvider.System), context, TimeProvider.System, new());
        var claims = await Task.WhenAll(Claims(first).TryClaimExecutionAsync(captured.JobId, "first"), Claims(second).TryClaimExecutionAsync(captured.JobId, "second"));
        var winner = claims.Where(claim => claim is not null).ShouldHaveSingleItem()!;
        winner.FenceToken.ShouldNotBe(captured.Fence);
        winner.Job.Request.ShouldBeOfType<MediaReconciliationRequest.SubjectWork>().Subject.ShouldBe(new MediaReconciliationSubject.Representation(graph.Cd.Id));
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(1);
        (await db.Database.SqlQuery<Guid>($"SELECT \"ExecutionFenceToken\" AS \"Value\" FROM romd.\"MediaReconciliationAttempts\" WHERE \"JobId\"={captured.JobId}").SingleAsync()).ShouldBe(captured.Fence);
        await RejectAsync(db, $"UPDATE romd.\"MediaReconciliationAttempts\" SET \"ExecutionFenceToken\"={captured.Fence} WHERE \"JobId\"={captured.JobId}", atCommit: false);
    }
}
