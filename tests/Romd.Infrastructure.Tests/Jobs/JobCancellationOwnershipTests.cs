using System.Data;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Jobs.Commands.CancelJob;
using Romd.Admin.Application.Source.Rom.Reconciliation;
using Romd.Application.Common.Security;
using Romd.Domain.Identity;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class JobCancellationOwnershipTests
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero).AddTicks(1234560);
    private static readonly JobVisibilityScope AllOrdinary = new(null, false, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyCancellationStagedAsync_CommitOrRollback_ChangesOnlyCancellationAndExecutionOwnership(bool commit)
    {
        using var database = PostgreSqlTestDatabase.Create();
        var id = await SeedAsync(database);
        await using var context = database.CreateContext();
        await using var observer = database.CreateContext();
        var before = await SnapshotAsync(observer, id);
        var repository = new JobRepository(context, new ManualTimeProvider(Now));
        var store = new JobCancellationStore(context, repository);
        await Should.ThrowAsync<InvalidOperationException>(() => store.LockVisibleForCancellationStagedAsync(id, AllOrdinary));

        await using (var transaction = await new EfUnitOfWork(context).BeginTransactionAsync())
        {
            var current = (await store.LockVisibleForCancellationStagedAsync(id, AllOrdinary)).ShouldBeOfType<UploadJob>();
            current.Cancel(Now);
            await store.ApplyCancellationStagedAsync(current);
            (await observer.Jobs.SingleAsync()).Phase.ShouldBe("IngestingRoms");
            (await context.Jobs.SingleAsync()).Phase.ShouldBe("Cancelled");
            if (commit)
                await transaction.CommitAsync();
        }

        var after = await SnapshotAsync(observer, id);
        string[] changed = ["Phase", "CompletedAt", "UpdatedAt", "ExecutionFenceToken", "ExecutionLeaseExpiresAtUtc"];
        foreach (var (property, value) in before)
            if (!commit || !changed.Contains(property))
                after[property].ShouldBe(value, property);
        if (commit)
        {
            after["Phase"].ShouldBe("Cancelled");
            after["CompletedAt"].ShouldBe(Persisted(Now));
            after["UpdatedAt"].ShouldBe(Persisted(Now));
            after["ExecutionFenceToken"].ShouldBeNull();
            after["ExecutionLeaseExpiresAtUtc"].ShouldBeNull();
        }
    }

    [Fact]
    public async Task ApplyCancellationStagedAsync_UnlockedObjectOrDifferentBoundary_RejectsBeforeMutation()
    {
        using var database = PostgreSqlTestDatabase.Create();
        var id = await SeedAsync(database);
        await using var context = database.CreateContext();
        var repository = new JobRepository(context, new ManualTimeProvider(Now));
        var store = new JobCancellationStore(context, repository);
        UploadJob retained;
        await using (var transaction = await new EfUnitOfWork(context).BeginTransactionAsync())
        {
            retained = (await store.LockVisibleForCancellationStagedAsync(id, AllOrdinary)).ShouldBeOfType<UploadJob>();
            var stale = (await repository.GetByIdAsync(id)).ShouldBeOfType<UploadJob>();
            stale.Cancel(Now);
            await Should.ThrowAsync<InvalidOperationException>(() => store.ApplyCancellationStagedAsync(stale));
            (await context.Jobs.SingleAsync()).Phase.ShouldBe("IngestingRoms");
        }
        await using (var transaction = await new EfUnitOfWork(context).BeginTransactionAsync())
        {
            retained.Cancel(Now);
            await Should.ThrowAsync<InvalidOperationException>(() => store.ApplyCancellationStagedAsync(retained));
        }
        await using var wrongIsolation = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        await Should.ThrowAsync<InvalidOperationException>(() => store.LockVisibleForCancellationStagedAsync(id, AllOrdinary));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleAsync_CompletionOrCheckpointWinsRowLock_RevalidatesCurrentStateAndPreservesAllProgress(bool terminal)
    {
        using var database = PostgreSqlTestDatabase.Create();
        var id = await SeedAsync(database);
        await using var writer = database.CreateContext();
        await using var blocker = await new EfUnitOfWork(writer).BeginTransactionAsync();
        var typed = new UploadJobRepository(writer, new ManualTimeProvider(Now));
        var winning = (await typed.GetByIdAsync(id)).ShouldNotBeNull();
        winning.RecordRomResult(RomIngestOutcome.Ingested, Now);
        winning.SetCurrentItem("latest-file.rom");
        winning.RecordError("latest-file.rom", "Preserved history", Now);
        winning.SetHangfireJobId("winning-delivery");
        if (terminal)
        {
            winning.Complete(Now);
            winning.Archive(Now);
        }
        string? expectedCurrentItem = winning.CurrentItem;
        await typed.UpdateStagedAsync(winning);
        await writer.SaveChangesAsync();
        await using var context = database.CreateContext();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int pid = await context.OpenBackendProcessIdAsync(timeout.Token);
        var clock = new ManualTimeProvider(Now.AddMinutes(-1));
        var delivery = Substitute.For<IJobDeliveryCancellation>();
        var handler = Handler(context, clock, delivery);
        var operation = handler.HandleAsync(new(id), timeout.Token);
        Exception? primary = null;
        try
        {
            (await database.WaitsForALockAsync(pid, operation, timeout.Token)).ShouldBeTrue();
            clock.SetUtcNow(Now.AddMinutes(1));
            await blocker.CommitAsync(timeout.Token);
            var result = await operation.WaitAsync(timeout.Token);
            result.IsError.ShouldBe(terminal);
            if (terminal)
                result.FirstError.Code.ShouldBe("Jobs.AlreadyComplete");
            await using var verify = database.CreateContext();
            var saved = await verify.Set<UploadJobEntity>().SingleAsync();
            saved.RomsIngested.ShouldBe(2);
            saved.CurrentItem.ShouldBe(expectedCurrentItem);
            saved.GetErrors().ShouldContain(error => error.Message == "Preserved history");
            saved.HangfireJobId.ShouldBe("winning-delivery");
            saved.Phase.ShouldBe(terminal ? "CompletedWithErrors" : "Cancelled");
            saved.IsArchived.ShouldBe(terminal);
            saved.CompletedAt.ShouldBe(Persisted(terminal ? Now : Now.AddMinutes(1)));
            if (terminal)
                delivery.ReceivedCalls().ShouldBeEmpty();
            else
                await delivery.Received(1).RequestStopAsync("winning-delivery", CancellationToken.None);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            // This command deliberately ignores HTTP cancellation after authorization. Release the
            // blocker before joining it, including when lock observation itself failed.
            await blocker.DisposeAsync();
            await PostgreSqlLockQueue.CancelAndJoinAsync(timeout, primary, operation);
        }
    }

    [Fact]
    public async Task HandleAsync_ClaimHandoffWinsLock_StopsWinningDeliveryAndRevokesItsFence()
    {
        using var database = PostgreSqlTestDatabase.Create();
        var id = await SeedAsync(database);
        await using var writer = database.CreateContext();
        await using var blocker = await new EfUnitOfWork(writer).BeginTransactionAsync();
        Guid winningFence = Guid.NewGuid();
        await writer.Jobs.Where(row => row.Id == id).ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.ExecutionFenceToken, winningFence)
            .SetProperty(row => row.ExecutionLeaseExpiresAtUtc, Now.AddHours(1))
            .SetProperty(row => row.HangfireJobId, "handoff-delivery"));
        await using var context = database.CreateContext();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int pid = await context.OpenBackendProcessIdAsync(timeout.Token);
        var delivery = Substitute.For<IJobDeliveryCancellation>();
        var operation = Handler(context, new ManualTimeProvider(Now), delivery).HandleAsync(new(id), timeout.Token);
        Exception? primary = null;
        try
        {
            (await database.WaitsForALockAsync(pid, operation, timeout.Token)).ShouldBeTrue();
            await blocker.CommitAsync(timeout.Token);
            (await operation.WaitAsync(timeout.Token)).IsError.ShouldBeFalse();
            await delivery.Received(1).RequestStopAsync("handoff-delivery", CancellationToken.None);
            await using var verify = database.CreateContext();
            var saved = await verify.Jobs.SingleAsync();
            saved.ExecutionFenceToken.ShouldBeNull();
            saved.ExecutionLeaseExpiresAtUtc.ShouldBeNull();
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            await blocker.DisposeAsync();
            await PostgreSqlLockQueue.CancelAndJoinAsync(timeout, primary, operation);
        }
    }

    [Theory]
    [InlineData("checkpoint")]
    [InlineData("defer")]
    [InlineData("claim")]
    public async Task HandleAsync_CancellationWinsLock_QueuedExecutionCannotResurrectJob(string operation)
    {
        using var database = PostgreSqlTestDatabase.Create();
        var id = await SeedAsync(database, scheduled: false, claimable: operation == "claim");
        await using var context = database.CreateContext();
        var clock = new ManualTimeProvider(Now);
        var repository = new JobRepository(context, clock);
        var store = new JobCancellationStore(context, repository);
        await using var boundary = await new EfUnitOfWork(context).BeginTransactionAsync();
        var current = (await store.LockVisibleForCancellationStagedAsync(id, AllOrdinary)).ShouldBeOfType<UploadJob>();
        current.Cancel(Now);
        await store.ApplyCancellationStagedAsync(current);
        await using var worker = database.CreateContext();
        var typed = new UploadJobRepository(worker, clock);
        var stale = (await typed.GetByIdAsync(id)).ShouldNotBeNull();
        var claims = new ClaimedJobRepository<UploadJob, UploadJobRepository>(typed, worker, clock, new());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pending = await database.StartWaitingForALockAsync(worker, async token => operation switch
        {
            "checkpoint" => await claims.TryUpdateClaimedAsync(stale, SeedFence, token),
            "defer" => await claims.TryDeferClaimedAsync(stale, SeedFence, Now.AddMinutes(2), token),
            _ => await claims.TryClaimExecutionAsync(id, "new-delivery", token) is not null
        }, timeout.Token);
        Exception? primary = null;
        try
        {
            pending.Waited.ShouldBeTrue();
            await boundary.CommitAsync(timeout.Token);
            (await pending.Pending).ShouldBeFalse();
            (await worker.Jobs.SingleAsync()).Phase.ShouldBe("Cancelled");
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            await boundary.DisposeAsync();
            await PostgreSqlLockQueue.CancelAndJoinAsync(timeout, primary, pending.Pending);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleAsync_FailureAfterFlush_RollsBackCancellationAndUnrelatedStagedStateWithoutStoppingDelivery(bool cancelled)
    {
        using var database = PostgreSqlTestDatabase.Create();
        var id = await SeedAsync(database);
        await using var context = database.CreateContext();
        var delivery = Substitute.For<IJobDeliveryCancellation>();
        var work = new BeforeCommit(new EfUnitOfWork(context), async () =>
        {
            await using var observer = database.CreateContext();
            (await observer.Jobs.SingleAsync()).Phase.ShouldBe("IngestingRoms");
            (await context.Jobs.SingleAsync(row => row.Id == id)).Phase.ShouldBe("Cancelled");
            throw cancelled ? new OperationCanceledException("Injected cancellation after flush.")
                : new IOException("Injected failure after flush.");
        });
        var handler = Handler(context, new ManualTimeProvider(Now), delivery, work);
        var staged = UploadJobEntity.FromDomain(UploadJob.Create("unrelated.zip", Now));
        context.Add(staged);

        if (cancelled)
            await Should.ThrowAsync<OperationCanceledException>(() => handler.HandleAsync(new(id)));
        else
            await Should.ThrowAsync<IOException>(() => handler.HandleAsync(new(id)));

        delivery.ReceivedCalls().ShouldBeEmpty();
        context.ChangeTracker.Entries().ShouldBeEmpty();
        await using var verify = database.CreateContext();
        (await verify.Jobs.SingleAsync()).Phase.ShouldBe("IngestingRoms");
        (await verify.Jobs.SingleAsync()).ExecutionFenceToken.ShouldBe(SeedFence);
        (await Handler(context, new ManualTimeProvider(Now), delivery).HandleAsync(new(id))).IsError.ShouldBeFalse();
        await delivery.Received(1).RequestStopAsync("original-delivery", CancellationToken.None);
    }

    private static DateTimeOffset Persisted(DateTimeOffset time) => DateTimeOffset.FromUnixTimeMilliseconds(time.ToUnixTimeMilliseconds());

    private static readonly Guid SeedFence = Guid.Parse("819197c6-18ea-4d4a-8ab0-33f6ad467b14");
    private static async Task<Guid> SeedAsync(PostgreSqlTestDatabase database, bool scheduled = true, bool claimable = false)
    {
        await using var db = database.CreateContext();
        var job = UploadJob.Create("source.zip", Now.AddHours(-1), createdByUserId: null);
        job.SetMaxParallelRoms(3);
        job.SetAllowUnidentified(true);
        job.SetArchiveOnly(true);
        job.SetTrackedOnly(true);
        job.SetImportSource("/source/roms", true);
        job.Start("original-delivery", Now.AddMinutes(-20));
        job.BeginClassification();
        job.SetDiscoveredFiles(2, 5);
        job.BeginDatIngestion();
        job.RecordDatResult(true, Now.AddMinutes(-10));
        job.BeginRomIngestion();
        job.RecordRomResult(RomIngestOutcome.Ingested, Now.AddMinutes(-5));
        job.SetCurrentItem("prior.rom");
        var row = UploadJobEntity.FromDomain(job, Now.AddMinutes(-5));
        row.ExecutionFenceToken = claimable ? null : SeedFence;
        row.ExecutionLeaseExpiresAtUtc = claimable ? null : Now.AddMinutes(5);
        row.NextAttemptAtUtc = scheduled ? Now.AddMinutes(2) : null;
        row.LastAttemptError = "Prior delivery interrupted";
        row.LastAttemptErrorTruncated = true;
        row.DeferralCount = 2;
        row.LastDeferralError = "Prior backoff";
        row.FirstDeferralAt = Now.AddMinutes(-8);
        row.LastDeferralAt = Now.AddMinutes(-5);
        db.Add(row);
        await db.SaveChangesAsync();
        return job.Id;
    }

    private static CancelJobCommandHandler Handler(RomdDbContext context, TimeProvider clock,
        IJobDeliveryCancellation delivery, IUnitOfWork? work = null)
    {
        var user = Substitute.For<ICurrentUser>();
        user.UserId.Returns(Guid.NewGuid());
        user.HasRole(Arg.Any<RomdRoleType>()).Returns(call => call.Arg<RomdRoleType>() <= RomdRoleType.Manager);
        var repository = new JobRepository(context, clock);
        return new(repository, new JobCancellationStore(context, repository), Substitute.For<IMediaReconciliationRequests>(),
            work ?? new EfUnitOfWork(context), user, clock, delivery, NullLogger<CancelJobCommandHandler>.Instance);
    }

    private static async Task<Dictionary<string, object?>> SnapshotAsync(RomdDbContext context, Guid id)
    {
        var row = await context.Set<UploadJobEntity>().SingleAsync(row => row.Id == id);
        return context.Entry(row).Properties.ToDictionary(property => property.Metadata.Name, property => property.CurrentValue);
    }

    private sealed class BeforeCommit(IUnitOfWork inner, Func<Task> afterFlush) : IUnitOfWork
    {
        public Task FlushAsync(CancellationToken cancellationToken = default) => inner.FlushAsync(cancellationToken);
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new Transaction(await inner.BeginTransactionAsync(cancellationToken), inner, afterFlush);
        private sealed class Transaction(ITransaction inner, IUnitOfWork work, Func<Task> afterFlush) : ITransaction
        {
            public async Task CommitAsync(CancellationToken cancellationToken = default)
            {
                await work.FlushAsync(cancellationToken);
                await afterFlush();
                await inner.CommitAsync(cancellationToken);
            }
            public Task RollbackAsync(CancellationToken cancellationToken = default) => inner.RollbackAsync(cancellationToken);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
