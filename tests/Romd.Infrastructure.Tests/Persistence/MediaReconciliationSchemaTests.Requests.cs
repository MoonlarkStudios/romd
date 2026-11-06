using Microsoft.EntityFrameworkCore;
using Npgsql;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories.Reconciliation;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed partial class MediaReconciliationSchemaTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Requests_DemandAuditAndDispatch_CommitWithCallerWorkOrRollbackTogether(bool commit)
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var demand = new MediaReconciliationDemand(Guid.NewGuid(), new MediaReconciliationSubject.LogicalRom(graph.RomId), false, MediaReconciliationCause.Readiness, null, null);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            db.Files.Add(new() { Id = 4, Sha256 = MediaFactGraphFixture.Hash('b'), Size = 5, SizeOnDisk = 5 });
            var accepted = await new MediaReconciliationRequests(db, TimeProvider.System).StageSubjectAsync(demand);
            accepted.IsError.ShouldBeFalse();
            accepted.Value.Created.ShouldBeTrue();
            await using (var observer = _database.CreateContext())
            {
                (await observer.Jobs.CountAsync()).ShouldBe(0);
                (await observer.Files.CountAsync()).ShouldBe(3);
            }
            await db.SaveChangesAsync();
            if (commit)
                await transaction.CommitAsync();
            else
                await transaction.RollbackAsync();
        }
        await using var after = _database.CreateContext();
        (await after.Jobs.CountAsync()).ShouldBe(commit ? 1 : 0);
        (await after.JobDispatches.CountAsync()).ShouldBe(commit ? 1 : 0);
        (await after.Set<MediaReconciliationSubjectEntity>().CountAsync()).ShouldBe(commit ? 1 : 0);
        (await after.Set<MediaReconciliationRequestEntity>().CountAsync()).ShouldBe(commit ? 1 : 0);
        (await after.Files.CountAsync()).ShouldBe(commit ? 4 : 3);
    }

    private static async Task<Guid> AddActorAsync(RomdDbContext db)
    {
        Guid actor = Guid.NewGuid();
        db.Users.Add(new() { Id = actor, UserName = actor.ToString("N"), NormalizedUserName = actor.ToString("N").ToUpperInvariant(), SecurityStamp = actor.ToString() });
        await db.SaveChangesAsync();
        return actor;
    }

    [Fact]
    public async Task Requests_SeveralAcceptancesInOneTransaction_PreserveEachVersionAndExplicitIntent()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        Guid actor = await AddActorAsync(db);
        var subject = new MediaReconciliationSubject.LogicalRom(graph.RomId);
        var automatic = new MediaReconciliationDemand(Guid.NewGuid(), subject, false, MediaReconciliationCause.Facts, null, null);
        var explicitRequest = new MediaReconciliationDemand(Guid.NewGuid(), subject, true, MediaReconciliationCause.Admin, actor, null);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var requests = new MediaReconciliationRequests(db, TimeProvider.System);
            var first = await requests.StageSubjectAsync(automatic);
            var second = await requests.StageSubjectAsync(explicitRequest);
            var repeated = await requests.StageSubjectAsync(explicitRequest);
            first.IsError.ShouldBeFalse();
            second.IsError.ShouldBeFalse();
            repeated.IsError.ShouldBeFalse();
            first.Value.Created.ShouldBeTrue();
            second.Value.Created.ShouldBeFalse();
            repeated.Value.Created.ShouldBeFalse();
            second.Value.Job.Id.ShouldBe(first.Value.Job.Id);
            repeated.Value.Job.Id.ShouldBe(first.Value.Job.Id);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        var demand = await db.Set<MediaReconciliationSubjectEntity>().SingleAsync();
        demand.RequestedVersion.ShouldBe(2);
        demand.AcknowledgedVersion.ShouldBe(0);
        demand.ConversionRequested.ShouldBeTrue();
        var audit = await db.Set<MediaReconciliationRequestEntity>().OrderBy(row => row.RequestedVersion).ToArrayAsync();
        audit.Select(row => row.Id).ShouldBe([automatic.RequestId, explicitRequest.RequestId]);
        audit.Select(row => row.RequestedVersion).ShouldBe([1L, 2L]);
        audit[1].ActorId.ShouldBe(actor);
        audit[0].ActorId.ShouldBeNull();
        (await db.JobDispatches.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Requests_ConcurrentAutomaticAndExplicitDemand_AdoptOneJobWithoutLosingConversion()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        Guid actor = await AddActorAsync(db);
        var subject = new MediaReconciliationSubject.LogicalRom(graph.RomId);
        async Task<MediaReconciliationAcceptance> AcceptAsync(MediaReconciliationDemand demand)
        {
            await using var context = _database.CreateContext();
            await using var transaction = await context.Database.BeginTransactionAsync();
            var accepted = await new MediaReconciliationRequests(context, TimeProvider.System).StageSubjectAsync(demand);
            accepted.IsError.ShouldBeFalse();
            await context.SaveChangesAsync();
            await transaction.CommitAsync();
            return accepted.Value;
        }
        var accepted = await Task.WhenAll(AcceptAsync(new(Guid.NewGuid(), subject, false, MediaReconciliationCause.Readiness, null, null)),
            AcceptAsync(new(Guid.NewGuid(), subject, true, MediaReconciliationCause.Admin, actor, null)));
        accepted.Count(item => item.Created).ShouldBe(1);
        accepted.Select(item => item.Job.Id).Distinct().ShouldHaveSingleItem();
        var state = await db.Set<MediaReconciliationSubjectEntity>().SingleAsync();
        state.RequestedVersion.ShouldBe(2);
        state.ConversionRequested.ShouldBeTrue();
        (await db.Jobs.CountAsync()).ShouldBe(1);
        (await db.JobDispatches.CountAsync()).ShouldBe(1);
        (await db.Set<MediaReconciliationRequestEntity>().CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task Requests_IdempotencyIdentityCannotMoveToAnotherSubject()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        Guid id = Guid.NewGuid();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var requests = new MediaReconciliationRequests(db, TimeProvider.System);
            (await requests.StageSubjectAsync(new(id, new MediaReconciliationSubject.LogicalRom(graph.RomId), false, MediaReconciliationCause.Readiness, null, null))).IsError.ShouldBeFalse();
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var rejected = await new MediaReconciliationRequests(db, TimeProvider.System).StageSubjectAsync(
                new(id, new MediaReconciliationSubject.Representation(graph.Cd.Id), false, MediaReconciliationCause.Readiness, null, null));
            rejected.IsError.ShouldBeTrue();
            rejected.FirstError.Code.ShouldBe("MediaReconciliation.RequestConflict");
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        (await db.Jobs.CountAsync()).ShouldBe(1);
        (await db.Set<MediaReconciliationRequestEntity>().CountAsync()).ShouldBe(1);
        (await db.Set<MediaReconciliationSubjectEntity>().SingleAsync()).RequestedVersion.ShouldBe(1);
    }

    [Fact]
    public async Task Requests_CancellingSubject_RefusesNewDemandWithoutAcknowledgingOrDroppingIt()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var captured = await CaptureAsync(db, graph.Cd, new MediaReconciliationSubject.LogicalRom(graph.RomId), TargetDecoder, 1);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE romd.\"MediaReconciliationJobs\" SET \"CancelRequested\"=true WHERE \"JobId\"={captured.JobId}");
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var rejected = await new MediaReconciliationRequests(db, TimeProvider.System).StageSubjectAsync(
                new(Guid.NewGuid(), new MediaReconciliationSubject.LogicalRom(graph.RomId), false, MediaReconciliationCause.Facts, null, null));
            rejected.IsError.ShouldBeTrue();
            rejected.FirstError.Code.ShouldBe("MediaReconciliation.Cancelling");
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        (await db.Set<MediaReconciliationSubjectEntity>().SingleAsync()).RequestedVersion.ShouldBe(1);
        (await db.Set<MediaReconciliationRequestEntity>().CountAsync()).ShouldBe(1);
        (await db.Set<MediaReconciliationPinEntity>().CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Requests_MissingTransaction_RefusesWithoutStagingCallerWork()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var error = await Should.ThrowAsync<InvalidOperationException>(() => new MediaReconciliationRequests(db, TimeProvider.System).StageSubjectAsync(
            new(Guid.NewGuid(), new MediaReconciliationSubject.LogicalRom(graph.RomId), false, MediaReconciliationCause.Readiness, null, null)));
        error.Message.ShouldContain("ReadCommitted");
        db.ChangeTracker.Entries().Any(entry => entry.State == EntityState.Added).ShouldBeFalse();
        (await db.Jobs.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Requests_SelectionReplay_RetainsCapturedHighWatermarksAfterLibraryGrowth()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        Guid request = Guid.NewGuid(), originalJob;
        async Task<MediaReconciliationAcceptance> SelectAsync(Guid id)
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var accepted = await new MediaReconciliationRequests(db, TimeProvider.System).StageSelectionAsync(id, new MediaReconciliationFilter.AllSubjects(),
                10, false, MediaReconciliationCause.Readiness, null);
            accepted.IsError.ShouldBeFalse();
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return accepted.Value;
        }
        originalJob = (await SelectAsync(request)).Job.Id;
        db.OriginalDiscLayouts.Add(new() { Id = 17, SourceJobId = Guid.NewGuid(), SourceRelativePath = "disc.cue", EntryPoint = "disc.cue", DescriptorFileId = 3 });
        await db.SaveChangesAsync();
        var repeated = await SelectAsync(request);
        var fresh = await SelectAsync(Guid.NewGuid());
        repeated.Created.ShouldBeFalse();
        repeated.Job.Id.ShouldBe(originalJob);
        repeated.Job.Request.ShouldBeOfType<MediaReconciliationRequest.SelectionWork>().Bounds.ShouldBe(new(graph.RomId, 0, graph.Cd.Id, 0));
        fresh.Created.ShouldBeTrue();
        fresh.Job.Request.ShouldBeOfType<MediaReconciliationRequest.SelectionWork>().Bounds.ShouldBe(new(graph.RomId, 17, graph.Cd.Id, 0));
        (await db.JobDispatches.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task Requests_MissingAttribution_RefusesCommitAndRollsBackDispatch()
    {
        var (db, _) = await SetupAsync();
        await using var owned = db;
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var job = MediaReconciliationJob.Create(Guid.NewGuid(), new MediaReconciliationRequest.SelectionWork(new MediaReconciliationFilter.AllSubjects(), new(0, 0, 0, 0), 1, false), "selection", null, null, DateTimeOffset.UnixEpoch);
            await new MediaReconciliationJobRepository(db, TimeProvider.System).AddAsync(job);
            await db.SaveChangesAsync();
            var error = await Should.ThrowAsync<PostgresException>(() => transaction.CommitAsync());
            error.SqlState.ShouldBe(PostgresErrorCodes.CheckViolation);
        }
        await using var observer = _database.CreateContext();
        (await observer.Jobs.CountAsync()).ShouldBe(0);
        (await observer.JobDispatches.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Requests_AdoptedChild_RetainsOriginalOwnerAndEverySelectionsAttribution()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var subject = new MediaReconciliationSubject.LogicalRom(graph.RomId);
        Guid firstParent, secondParent, child;
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var requests = new MediaReconciliationRequests(db, TimeProvider.System);
            var first = await requests.StageSelectionAsync(Guid.NewGuid(), new MediaReconciliationFilter.AllSubjects(), 1, false, MediaReconciliationCause.Readiness, null);
            var second = await requests.StageSelectionAsync(Guid.NewGuid(), new MediaReconciliationFilter.AllSubjects(), 1, false, MediaReconciliationCause.Readiness, null);
            first.IsError.ShouldBeFalse();
            second.IsError.ShouldBeFalse();
            firstParent = first.Value.Job.Id;
            secondParent = second.Value.Job.Id;
            var created = await requests.StageSubjectAsync(new(Guid.NewGuid(), subject, false, MediaReconciliationCause.Selection, null, firstParent));
            var adopted = await requests.StageSubjectAsync(new(Guid.NewGuid(), subject, false, MediaReconciliationCause.Selection, null, secondParent));
            created.IsError.ShouldBeFalse();
            adopted.IsError.ShouldBeFalse();
            child = created.Value.Job.Id;
            created.Value.Created.ShouldBeTrue();
            created.Value.OwnedBySelection.ShouldBeTrue();
            adopted.Value.Created.ShouldBeFalse();
            adopted.Value.OwnedBySelection.ShouldBeFalse();
            adopted.Value.Job.Id.ShouldBe(child);
            await db.SaveChangesAsync();
            // The selecting workers hold explicit live execution fences; ownership and counters publish with the child acceptance.
            Guid firstFence = Guid.NewGuid(), secondFence = Guid.NewGuid();
            long lease = DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE romd."Jobs" SET "Phase"='Selecting',"ExecutionFenceToken"={firstFence},"ExecutionLeaseExpiresAtUtc"={lease} WHERE "Id"={firstParent};
                UPDATE romd."Jobs" SET "Phase"='Selecting',"ExecutionFenceToken"={secondFence},"ExecutionLeaseExpiresAtUtc"={lease} WHERE "Id"={secondParent};
                INSERT INTO romd."MediaReconciliationSelectionItems" ("SelectionJobId","SubjectKind","SubjectId","Ordinal","ChildJobId","IsOwned")
                    VALUES ({firstParent},'LogicalRom',{subject.Id},1,{child},true),({secondParent},'LogicalRom',{subject.Id},1,{child},false);
                UPDATE romd."MediaReconciliationJobs" SET "SelectedCount"=1,"CreatedCount"=1,"CursorKind"='LogicalRom',"CursorId"={subject.Id} WHERE "JobId"={firstParent};
                UPDATE romd."MediaReconciliationJobs" SET "SelectedCount"=1,"AdoptedCount"=1,"CursorKind"='LogicalRom',"CursorId"={subject.Id} WHERE "JobId"={secondParent};
                """);
            await transaction.CommitAsync();
        }
        var current = (await new MediaReconciliationJobRepository(db, TimeProvider.System).GetByIdAsync(child)).ShouldNotBeNull();
        current.OriginSelectionJobId.ShouldBe(firstParent);
        (await db.Set<MediaReconciliationRequestEntity>().Where(row => row.JobId == child).Select(row => row.OriginSelectionJobId).ToArrayAsync())
            .ShouldBe([firstParent, secondParent], ignoreOrder: true);
        (await db.Set<MediaReconciliationSubjectEntity>().SingleAsync()).RequestedVersion.ShouldBe(2);
        (await db.JobDispatches.CountAsync()).ShouldBe(3);
    }

    [Fact]
    public async Task Requests_ReusedContextAfterIndependentCompletion_CreatesFreshDemandInsteadOfAdoptingStaleState()
    {
        var (db, graph) = await SetupAsync();
        await using var owned = db;
        var subject = new MediaReconciliationSubject.LogicalRom(graph.RomId);
        var requests = new MediaReconciliationRequests(db, TimeProvider.System);
        Guid first;
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var accepted = await requests.StageSubjectAsync(new(Guid.NewGuid(), subject, false, MediaReconciliationCause.Readiness, null, null));
            accepted.IsError.ShouldBeFalse();
            first = accepted.Value.Job.Id;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        // Another worker settles the no-work plan while this context still has its original tracked rows.
        await using (var worker = _database.CreateContext())
        await using (var transaction = await worker.Database.BeginTransactionAsync())
        {
            Guid fence = Guid.NewGuid();
            var completed = DateTimeOffset.UtcNow;
            await worker.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE romd."Jobs" SET "ExecutionFenceToken"={fence},"ExecutionLeaseExpiresAtUtc"={completed.AddMinutes(2).ToUnixTimeMilliseconds()} WHERE "Id"={first};
                INSERT INTO romd."MediaReconciliationOutcomes" ("JobId","Kind","Code","AcknowledgedVersion","ExecutionFenceToken","CompletedAtUtcTicks")
                    VALUES ({first},'Completed','no-work',1,{fence},{completed.UtcTicks});
                UPDATE romd."MediaReconciliationSubjects" SET "AcknowledgedVersion"=1 WHERE "LatestJobId"={first};
                UPDATE romd."Jobs" SET "Phase"='Completed',"CompletedAt"={completed.ToUnixTimeMilliseconds()} WHERE "Id"={first};
                """);
            await transaction.CommitAsync();
        }
        Guid next;
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var accepted = await requests.StageSubjectAsync(new(Guid.NewGuid(), subject, false, MediaReconciliationCause.Facts, null, null));
            accepted.IsError.ShouldBeFalse();
            accepted.Value.Created.ShouldBeTrue();
            next = accepted.Value.Job.Id;
            next.ShouldNotBe(first);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        await using var observer = _database.CreateContext();
        var demand = await observer.Set<MediaReconciliationSubjectEntity>().SingleAsync();
        demand.LatestJobId.ShouldBe(next);
        demand.RequestedVersion.ShouldBe(2);
        demand.AcknowledgedVersion.ShouldBe(1);
        (await observer.JobDispatches.CountAsync()).ShouldBe(2);
        (await new MediaReconciliationJobRepository(observer, TimeProvider.System).GetByIdAsync(first)).ShouldNotBeNull().Outcome.ShouldBe(MediaReconciliationOutcomeKind.Completed);
    }
}
