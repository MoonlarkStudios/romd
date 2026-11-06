using System.Data;
using Microsoft.EntityFrameworkCore;
using Romd.Admin.Application.Source.Rom;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories.Reconciliation;
using Romd.Domain.Hashing;
using Romd.Domain.Source.Rom;
using Romd.Persistence;
using Romd.Persistence.MediaFacts;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

/// <summary>Real publication/locking behavior over complete synthetic facts, without media qualification.</summary>
public sealed class RomMediaObservationStoreTests : IAsyncLifetime
{
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
    private static readonly DateTimeOffset Time = DateTimeOffset.UnixEpoch.AddTicks(1234567);
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StageAsync_CancellingSubject_ReturnsConflictAndCallerRollbackRetainsNoUnscheduledFact(bool chd)
    {
        await using var db = _database.CreateContext();
        var seed = await LogicalInspectionFixture.PublishAsync(db, Time, retain: false);
        Guid jobId;
        await using (var transaction = await new EfUnitOfWork(db).BeginTransactionAsync())
        {
            var accepted = await new MediaReconciliationRequests(db, TimeProvider.System).StageSubjectAsync(new(Guid.NewGuid(),
                new MediaReconciliationSubject.LogicalRom(seed.RomId), false, MediaReconciliationCause.Readiness, null, null));
            accepted.IsError.ShouldBeFalse();
            jobId = accepted.Value.Job.Id;
            await transaction.CommitAsync();
        }
        await using (var transaction = await new EfUnitOfWork(db).BeginTransactionAsync())
        {
            (await new MediaReconciliationRequests(db, TimeProvider.System).StageCancellationAsync(jobId)).IsError.ShouldBeFalse();
            await transaction.CommitAsync();
        }
        await using (var transaction = await new EfUnitOfWork(db).BeginTransactionAsync())
        {
            var rejected = await Store(db).StageAsync(seed.RomId, Receipt(chd ? seed.Chd : seed.Original), seed.Observation);
            rejected.FirstError.Code.ShouldBe("MediaReconciliation.Cancelling");
            (await CountAsync(db)).ShouldBe(1);
            await transaction.RollbackAsync();
        }
        await using var observer = _database.CreateContext();
        (await CountAsync(observer)).ShouldBe(0);
        (await observer.Set<MediaReconciliationRequestEntity>().CountAsync()).ShouldBe(1);
        (await observer.Set<MediaReconciliationSubjectEntity>().SingleAsync()).RequestedVersion.ShouldBe(1);
        (await observer.Verifications.CountAsync()).ShouldBe(2);
    }

    [Theory]
    [InlineData(IsolationLevel.ReadUncommitted)]
    [InlineData(IsolationLevel.RepeatableRead)]
    [InlineData(IsolationLevel.Serializable)]
    public async Task StageAsync_WrongIsolation_RefusesWithoutFactsOrWakeup(IsolationLevel isolation)
    {
        await using var db = _database.CreateContext();
        var seed = await LogicalInspectionFixture.PublishAsync(db, Time, false);
        await using var transaction = await db.Database.BeginTransactionAsync(isolation);
        await Should.ThrowAsync<InvalidOperationException>(() => Store(db).StageAsync(seed.RomId, Receipt(seed.Original), seed.Observation));
        (await CountAsync(db)).ShouldBe(0);
        (await db.Set<Romd.Persistence.Entities.MediaReconciliationRequestEntity>().CountAsync()).ShouldBe(0);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task StageAsync_UnrecognizedLayoutFamily_RefusesWithoutDroppingTheLayoutOrWakingReconciliation()
    {
        await using var db = _database.CreateContext();
        var seed = await LogicalInspectionFixture.PublishAsync(db, Time, false);
        // The domain refuses foreign families at construction; an uninitialized instance models one that bypassed it.
        var foreign = (ForeignLayout)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ForeignLayout));
        var observation = new RomLogicalInspection(Guid.NewGuid(), seed.Observation.Identity, "foreign-structure/v1", "foreign-inspector",
            Time, seed.Observation.Prefix, foreign);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);

        var refused = await Should.ThrowAsync<ArgumentException>(() => Store(db).StageAsync(seed.RomId, Receipt(seed.Original), observation));

        refused.ParamName.ShouldBe("observation");
        (await CountAsync(db)).ShouldBe(0);
        (await db.Set<Romd.Persistence.Entities.MediaReconciliationRequestEntity>().CountAsync()).ShouldBe(0);
        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StageAsync_RawFactsAndWakeupAreAtomicWithoutFlushingUnrelatedCallerState(bool chd)
    {
        await using var db = _database.CreateContext();
        var seed = await LogicalInspectionFixture.PublishAsync(db, Time, false);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var pending = new Romd.Persistence.Entities.FileEntityPersistence { Sha256 = MediaFactGraphFixture.Hash('9'), Size = 1, SizeOnDisk = 1 };
        db.Files.Add(pending);
        (await Store(db).StageAsync(seed.RomId, Receipt(chd ? seed.Chd : seed.Original), seed.Observation)).IsError.ShouldBeFalse();
        db.Database.CurrentTransaction.ShouldBeSameAs(transaction);
        db.Entry(pending).State.ShouldBe(EntityState.Added);
        (await CountAsync(db)).ShouldBe(1);
        db.Set<Romd.Persistence.Entities.MediaReconciliationRequestEntity>().Local.ShouldHaveSingleItem().Id.ShouldBe(seed.Observation.Id);
        await AssertTupleGuardsAsync(db, 1);
        await using (var outside = _database.CreateContext())
        {
            (await CountAsync(outside)).ShouldBe(0);
            (await outside.Set<Romd.Persistence.Entities.MediaReconciliationRequestEntity>().CountAsync()).ShouldBe(0);
            (await outside.Files.CountAsync()).ShouldBe(2);
        }
        await transaction.RollbackAsync();
        (await CountAsync(db)).ShouldBe(0);
        (await db.Set<Romd.Persistence.Entities.MediaReconciliationRequestEntity>().CountAsync()).ShouldBe(0);
        (await db.Verifications.CountAsync()).ShouldBe(2);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("file")]
    [InlineData("output")]
    [InlineData("representation")]
    [InlineData("proof")]
    [InlineData("released-output")]
    [InlineData("logical-hash")]
    [InlineData("changed-replay")]
    public async Task StageAsync_RejectedBindings_NeverAcquireAssertionGuardOrWakeup(string change)
    {
        await using var db = _database.CreateContext();
        var seed = await LogicalInspectionFixture.PublishAsync(db, Time, change == "changed-replay");
        await using var transaction = await db.Database.BeginTransactionAsync();
        var receipt = Receipt(seed.Original);
        var observation = seed.Observation;
        int romId = seed.RomId;
        receipt = change switch
        {
            "file" => receipt with { FileId = seed.Chd.FileId },
            "output" => receipt with { OutputId = seed.Chd.Outputs.Single().Id },
            "representation" => receipt with { RepresentationId = seed.Chd.Id },
            "proof" => receipt with { VerificationId = seed.Chd.Verifications.Single().Id },
            _ => receipt
        };
        if (change == "owner")
            romId = 999;
        if (change == "released-output")
        {
            var repository = MediaTestServices.CreateRomRepository(db);
            await repository.SetPreferredRepresentationStagedAsync(romId, seed.Chd.Outputs.Single().Id);
            await repository.ReleaseRepresentationsStagedAsync(romId, [seed.Original.Outputs.Single().Id]);
        }
        if (change == "logical-hash")
            observation = new(Guid.NewGuid(), new(131072, MediaFactGraphFixture.Hash('f'), LogicalInspectionFixture.Identity.Sha1,
                LogicalInspectionFixture.Identity.Md5, LogicalInspectionFixture.Identity.Crc32), observation.Profile, observation.InspectorBuild, Time, new byte[32], layout: LogicalInspectionFixture.Boot);
        if (change == "changed-replay")
            observation = new(Guid.NewGuid(), observation.Identity, observation.Profile, observation.InspectorBuild, Time, new byte[32],
                layout: new RomPs2BootLayoutFacts(131072, 63, 16, 24, 200, 25, 123, 28, 896, @"cdrom0:\slus_203.12;1", @"cdrom0:\SLUS_203.12;1", "SLUS_203.12", 0x100000, 2,
                    RomPs2BootLayoutFacts.ProfileVersion, "fixture-boot-inspector"));
        (await Store(db).StageAsync(romId, receipt, observation)).IsError.ShouldBeTrue();
        await AssertTupleGuardsAsync(db, 0);
        (await CountAsync(db)).ShouldBe(change == "changed-replay" ? 1 : 0);
        var requests = await db.Set<Romd.Persistence.Entities.MediaReconciliationRequestEntity>().ToListAsync();
        if (change == "changed-replay")
            requests.ShouldHaveSingleItem().RequestedVersion.ShouldBe(1);
        else
            requests.ShouldBeEmpty();
        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StageAsync_ExactReplayFromAnotherRetainedSource_PreservesFirstIdentityInstantAndWakeup(bool chd)
    {
        await using var db = _database.CreateContext();
        var seed = await LogicalInspectionFixture.PublishAsync(db, Time, true);
        var replay = new RomLogicalInspection(Guid.NewGuid(), seed.Observation.Identity, seed.Observation.Profile, seed.Observation.InspectorBuild,
            Time.AddTicks(19), new byte[32], layout: LogicalInspectionFixture.Boot);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            (await Store(db).StageAsync(seed.RomId, Receipt(chd ? seed.Chd : seed.Original), replay)).IsError.ShouldBeFalse();
            await AssertTupleGuardsAsync(db, 0);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        var retained = (await MediaTestServices.CreateContentCatalog(db).FindByRomIdAsync(seed.RomId)).ShouldNotBeNull().Inspections.ShouldHaveSingleItem();
        retained.Id.ShouldBe(seed.Observation.Id);
        retained.ObservedAt.ShouldBe(Time);
        retained.SameObservation(seed.Observation).ShouldBeTrue();
        (await db.Set<Romd.Persistence.Entities.MediaReconciliationRequestEntity>().SingleAsync()).RequestedVersion.ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StageAsync_BlockedReferenceTuple_PublishesNeitherObservationNorWakeupBeforeGuardRelease(bool chd)
    {
        await using var seedDb = _database.CreateContext();
        var seed = await LogicalInspectionFixture.PublishAsync(seedDb, Time, false);
        await using var holder = _database.CreateContext();
        await using var held = await holder.Database.BeginTransactionAsync();
        await RomPs2ReferenceAssertionLock.AcquireAsync(holder, Query, CancellationToken.None);
        await using var writer = _database.CreateContext();
        await writer.Database.OpenConnectionAsync();
        int pid = await writer.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
        await using var transaction = await writer.Database.BeginTransactionAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var task = Store(writer).StageAsync(seed.RomId, Receipt(chd ? seed.Chd : seed.Original), seed.Observation, deadline.Token);
        try
        {
            await WaitForTupleAsync(pid, task, deadline.Token);
            task.IsCompleted.ShouldBeFalse();
            await using (var outside = _database.CreateContext())
            { (await CountAsync(outside)).ShouldBe(0); (await outside.Set<Romd.Persistence.Entities.MediaReconciliationRequestEntity>().CountAsync()).ShouldBe(0); }
            await held.CommitAsync(deadline.Token);
            (await task).IsError.ShouldBeFalse();
            await AssertTupleGuardsAsync(writer, 1);
            (await CountAsync(writer)).ShouldBe(1);
            writer.Set<Romd.Persistence.Entities.MediaReconciliationRequestEntity>().Local.ShouldHaveSingleItem().RequestedVersion.ShouldBe(1);
            await writer.SaveChangesAsync(deadline.Token);
            await transaction.CommitAsync(deadline.Token);
        }
        finally
        {
            deadline.Cancel();
            try
            { await task; }
            catch (OperationCanceledException) { }
        }
        await using var observer = _database.CreateContext();
        (await CountAsync(observer)).ShouldBe(1);
        (await observer.Set<Romd.Persistence.Entities.MediaReconciliationRequestEntity>().SingleAsync()).Id.ShouldBe(seed.Observation.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StageAsync_ConcurrentInspectionBuild_SerializesFirstCompleteObservation(bool conflicting)
    {
        await using var seedDb = _database.CreateContext();
        var seed = await LogicalInspectionFixture.PublishAsync(seedDb, Time, false);
        await using var first = _database.CreateContext();
        await using var firstTransaction = await first.Database.BeginTransactionAsync();
        (await Store(first).StageAsync(seed.RomId, Receipt(seed.Original), seed.Observation)).IsError.ShouldBeFalse();
        await using var second = _database.CreateContext();
        await second.Database.OpenConnectionAsync();
        int pid = await second.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
        await using var secondTransaction = await second.Database.BeginTransactionAsync();
        var prefix = new byte[32];
        if (conflicting)
            prefix[0] = 1;
        var replay = new RomLogicalInspection(Guid.NewGuid(), seed.Observation.Identity, seed.Observation.Profile, seed.Observation.InspectorBuild,
            Time.AddTicks(19), prefix, layout: LogicalInspectionFixture.Boot);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var task = Store(second).StageAsync(seed.RomId, Receipt(seed.Chd), replay, deadline.Token);
        try
        {
            (await _database.WaitsForALockAsync(pid, task, deadline.Token)).ShouldBeTrue();
            task.IsCompleted.ShouldBeFalse();
            await first.SaveChangesAsync(deadline.Token);
            await firstTransaction.CommitAsync(deadline.Token);
            (await task).IsError.ShouldBe(conflicting);
            await second.SaveChangesAsync(deadline.Token);
            await secondTransaction.CommitAsync(deadline.Token);
        }
        finally { deadline.Cancel(); try { await task; } catch (OperationCanceledException) { } }
        await using var observer = _database.CreateContext();
        var retained = (await MediaTestServices.CreateContentCatalog(observer).FindByRomIdAsync(seed.RomId)).ShouldNotBeNull().Inspections.ShouldHaveSingleItem();
        retained.Id.ShouldBe(seed.Observation.Id);
        retained.ObservedAt.ShouldBe(Time);
        retained.SameObservation(seed.Observation).ShouldBeTrue();
        (await observer.Set<Romd.Persistence.Entities.MediaReconciliationRequestEntity>().SingleAsync()).RequestedVersion.ShouldBe(1);
    }

    private async Task WaitForTupleAsync(int pid, Task pending, CancellationToken ct)
    {
        await using var observer = _database.CreateContext();
        while (!pending.IsCompleted)
        {
            if (await observer.Database.SqlQuery<bool>($"""
                SELECT EXISTS(SELECT 1 FROM pg_locks WHERE locktype='advisory' AND pid={pid}
                    AND classid::bigint=149 AND objid::bigint=1959562223 AND objsubid=2 AND NOT granted) AS "Value"
                """).SingleAsync(ct))
                return;
            await Task.Delay(10, ct);
        }
        await pending;
        throw new InvalidOperationException("Publication completed before waiting on the held reference tuple guard.");
    }

    private sealed record ForeignLayout : RomLayoutObservation
    {
        public ForeignLayout(RomLayoutObservation original) : base(original) { }
        public override bool Describes(RomLogicalIdentity identity) => true;
    }

    private static RomMediaObservationStore Store(RomdDbContext db) => new(db, new Romd.Persistence.Repositories.Reconciliation.MediaReconciliationRequests(db, TimeProvider.System));
    private static RomRetainedMeasurement Receipt(RomRepresentationFacts source) => LogicalInspectionFixture.Receipt(source);
    private static RomDiscMediaReferenceQuery Query => new(131072, LogicalInspectionFixture.Identity.Sha1, LogicalInspectionFixture.Identity.Md5, Crc32.FromUInt32(0));
    private static Task<int> CountAsync(RomdDbContext db) => db.Database.SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM romd.\"LogicalInspections\"").SingleAsync();
    private static async Task AssertTupleGuardsAsync(RomdDbContext db, int expected)
    {
        (await db.Database.SqlQueryRaw<int>("""
            SELECT count(*)::integer AS "Value" FROM pg_locks WHERE locktype='advisory' AND pid=pg_backend_pid()
                AND granted AND classid::bigint=149 AND objid::bigint=1959562223 AND objsubid=2
            """).SingleAsync()).ShouldBe(expected);
    }
}
