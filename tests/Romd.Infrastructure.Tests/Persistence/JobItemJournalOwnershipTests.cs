using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class JobItemJournalOwnershipTests
{
    private static readonly DateTimeOffset Now = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddRangeStagedAsync_OwnerFlushAndCommitOrRollback_PreservesIdsAndUnrelatedTracking(bool commit)
    {
        using var database = PostgreSqlTestDatabase.Create();
        var (claim, clock) = await SeedClaimAsync(database);
        await using var db = database.CreateContext();
        await using var observer = database.CreateContext();
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, clock);
        var work = new EfUnitOfWork(db);
        var unrelated = UploadJobEntity.FromDomain(UploadJob.Create("unrelated.zip", Now));
        var titleIds = new List<int> { 3, 8 };
        var legacy = JobItem.ForRom(claim.Job.Id, "folder/a.rom", 42, RomIngestOutcome.Ingested,
            7, titleIds, 9, null, Now, archiveOnly: true);
        var stable = JobItem.ForRom(claim.Job.Id, "folder/b.rom", 43, RomIngestOutcome.Rejected,
            null, null, null, "policy", Now.AddSeconds(1)).WithUploadIdentity();
        titleIds.Clear();
        await using (var boundary = await work.BeginTransactionAsync())
        {
            db.Add(unrelated);
            await new JobItemRepository(db).AddRangeStagedAsync([legacy, stable]);

            db.Entry(unrelated).State.ShouldBe(EntityState.Added);
            db.ChangeTracker.Entries<JobItemEntity>().Count().ShouldBe(2);
            (await db.JobItems.CountAsync()).ShouldBe(0);
            (await db.Jobs.CountAsync(row => row.Id == unrelated.Id)).ShouldBe(0);
            await work.FlushAsync();
            (await db.JobItems.CountAsync()).ShouldBe(2);
            (await observer.JobItems.CountAsync()).ShouldBe(0);
            (await observer.Jobs.CountAsync(row => row.Id == unrelated.Id)).ShouldBe(0);
            if (commit)
                await boundary.CommitAsync();
        }

        (await observer.JobItems.CountAsync()).ShouldBe(commit ? 2 : 0);
        (await observer.Jobs.CountAsync(row => row.Id == unrelated.Id)).ShouldBe(commit ? 1 : 0);
        if (!commit)
        {
            db.ChangeTracker.Entries().ShouldBeEmpty();
            return;
        }
        var rows = await new JobItemRepository(observer).GetOutcomesAsync(claim.Job.Id);
        rows.Select(row => row.Id).ShouldBe([legacy.Id, stable.Id]);
        rows[0].MatchedTitleIds.ShouldBe([3, 8]);
        rows[0].ArchiveOnly.ShouldBe(true);
        rows[0].RomFileId.ShouldBe(7);
        rows[0].PlatformId.ShouldBe(9);
        rows[0].SizeBytes.ShouldBe(42);
        rows[0].CreatedAt.ShouldBe(Now);
        rows[1].Id.ShouldBe(JobItem.UploadIdentity(claim.Job.Id, JobItemKind.Rom, "folder/b.rom"));
        rows[1].Error.ShouldBe("policy");
    }

    [Theory]
    [InlineData("no-transaction")]
    [InlineData("no-fence")]
    [InlineData("unowned-transaction")]
    [InlineData("different-fence")]
    [InlineData("mixed-jobs")]
    public async Task AddRangeStagedAsync_UnownedBatch_RejectsBeforeTracking(string scenario)
    {
        using var database = PostgreSqlTestDatabase.Create();
        var (claim, clock) = await SeedClaimAsync(database);
        await using var db = database.CreateContext();
        var repository = new JobItemRepository(db);
        var item = Item(claim.Job.Id);
        if (scenario == "no-transaction")
        {
            await Should.ThrowAsync<InvalidOperationException>(() => repository.AddRangeStagedAsync([item]));
        }
        else if (scenario == "no-fence")
        {
            await using var boundary = await new EfUnitOfWork(db).BeginTransactionAsync();
            await Should.ThrowAsync<JobExecutionOwnershipLostException>(() => repository.AddRangeStagedAsync([item]));
        }
        else
        {
            using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, clock);
            if (scenario == "unowned-transaction")
            {
                await using var raw = await db.Database.BeginTransactionAsync();
                await Should.ThrowAsync<JobExecutionOwnershipLostException>(() => repository.AddRangeStagedAsync([item]));
            }
            else
            {
                await using var boundary = await new EfUnitOfWork(db).BeginTransactionAsync();
                using var changed = scenario == "different-fence"
                    ? JobExecutionFenceScope.Enter(claim.Job.Id, Guid.NewGuid(), clock) : null;
                IReadOnlyList<JobItem> items = scenario == "mixed-jobs" ? [item, Item(Guid.NewGuid())] : [item];
                await Should.ThrowAsync<JobExecutionOwnershipLostException>(() => repository.AddRangeStagedAsync(items));
                db.ChangeTracker.Entries<JobItemEntity>().ShouldBeEmpty();
            }
        }
        db.ChangeTracker.Entries<JobItemEntity>().ShouldBeEmpty();
        (await db.JobItems.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task AddRangeStagedAsync_PendingAndCommittedReplay_InsertsOnceWithoutOverwritingFirstSnapshot()
    {
        using var database = PostgreSqlTestDatabase.Create();
        var (claim, clock) = await SeedClaimAsync(database);
        var original = Item(claim.Job.Id);
        var changed = JobItem.Rehydrate(original.Id, original.JobId, original.Kind, original.FileName, 999,
            JobItemOutcome.Failed, null, null, null, [], null, "must not replace", null, Now.AddDays(1));
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, clock);
        await using var db = database.CreateContext();
        var repository = new JobItemRepository(db);
        var work = new EfUnitOfWork(db);
        await using (var boundary = await work.BeginTransactionAsync())
        {
            await repository.AddRangeStagedAsync([original, original]);
            await repository.AddRangeStagedAsync([changed]);
            db.ChangeTracker.Entries<JobItemEntity>().ShouldHaveSingleItem();
            (await db.JobItems.CountAsync()).ShouldBe(0);
            await boundary.CommitAsync();
        }
        db.ChangeTracker.Clear();
        // A lost commit acknowledgement repeats the exact key in a fresh boundary.
        await using (var boundary = await work.BeginTransactionAsync())
        {
            await repository.AddRangeStagedAsync([changed]);
            db.ChangeTracker.Entries<JobItemEntity>().ShouldBeEmpty();
            await boundary.CommitAsync();
        }
        var row = (await repository.GetOutcomesAsync(claim.Job.Id)).ShouldHaveSingleItem();
        row.Id.ShouldBe(original.Id);
        row.SizeBytes.ShouldBe(original.SizeBytes);
        row.Outcome.ShouldBe(original.Outcome);
        row.Error.ShouldBe(original.Error);
        row.CreatedAt.ShouldBe(original.CreatedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddRangeStagedAsync_FaultOrCancellationAfterRealFlush_RollsBackAndAllowsSameContextReplay(bool cancelled)
    {
        using var database = PostgreSqlTestDatabase.Create();
        var (claim, clock) = await SeedClaimAsync(database);
        var fault = new FailJournalFlushOnce(database, cancelled);
        await using var db = new RomdDbContext(database.CreateOptions(options => options.AddInterceptors(fault)));
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, clock);
        var repository = new JobItemRepository(db);
        var work = new EfUnitOfWork(db);
        var item = Item(claim.Job.Id);
        await using (var boundary = await work.BeginTransactionAsync())
        {
            await repository.AddRangeStagedAsync([item]);
            fault.Fired.ShouldBeFalse();
            if (cancelled)
            {
                OperationCanceledException? observed = null;
                try
                {
                    await boundary.CommitAsync();
                }
                catch (OperationCanceledException error)
                {
                    observed = error;
                }
                observed.ShouldBeSameAs(fault.Failure);
            }
            else
                (await Should.ThrowAsync<IOException>(() => boundary.CommitAsync())).ShouldBeSameAs(fault.Failure);
        }
        fault.Fired.ShouldBeTrue();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await using var observer = database.CreateContext();
        (await observer.JobItems.CountAsync()).ShouldBe(0);
        (await observer.Jobs.SingleAsync()).ExecutionFenceToken.ShouldBe(claim.FenceToken);
        await using (var retry = await work.BeginTransactionAsync())
        {
            await repository.AddRangeStagedAsync([item]);
            await retry.CommitAsync();
        }
        (await observer.JobItems.SingleAsync()).Id.ShouldBe(item.Id);
    }

    [Fact]
    public async Task AddRangeStagedAsync_LeaseExpiresWhileOwned_HoldsTakeoverUntilBatchCommits()
    {
        using var database = PostgreSqlTestDatabase.Create();
        var (claim, clock) = await SeedClaimAsync(database);
        await using var db = database.CreateContext();
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, clock);
        await using var boundary = await new EfUnitOfWork(db).BeginTransactionAsync();
        clock.Advance(new JobExecutionClaimOptions().LeaseDuration + TimeSpan.FromSeconds(1));
        await using var contender = database.CreateContext();
        var claims = new ClaimedJobRepository<UploadJob, UploadJobRepository>(new(contender, clock), contender, clock, new());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pending = await database.StartWaitingForALockAsync(contender,
            token => claims.TryClaimExecutionAsync(claim.Job.Id, "replacement", token), timeout.Token);
        Exception? primary = null;
        try
        {
            pending.Waited.ShouldBeTrue();
            var item = Item(claim.Job.Id);
            await new JobItemRepository(db).AddRangeStagedAsync([item]);
            await boundary.CommitAsync(timeout.Token);
            var replacement = (await pending.Pending).ShouldNotBeNull();
            replacement.FenceToken.ShouldNotBe(claim.FenceToken);
            (await contender.JobItems.SingleAsync()).Id.ShouldBe(item.Id);
            await boundary.DisposeAsync();
            await Should.ThrowAsync<JobExecutionOwnershipLostException>(() => new EfUnitOfWork(db).BeginTransactionAsync());
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            await boundary.DisposeAsync();
            await PostgreSqlLockQueue.CancelAndJoinAsync(timeout, primary, pending.Pending);
        }
    }

    [Fact]
    public async Task AddRangeStagedAsync_CallerChangesListDuringLookup_UsesCapturedBatch()
    {
        using var database = PostgreSqlTestDatabase.Create();
        var (claim, clock) = await SeedClaimAsync(database);
        var gate = new PauseJournalLookup();
        await using var db = new RomdDbContext(database.CreateOptions(options => options.AddInterceptors(gate)));
        using var fence = JobExecutionFenceScope.Enter(claim.Job.Id, claim.FenceToken, clock);
        await using var boundary = await new EfUnitOfWork(db).BeginTransactionAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var item = Item(claim.Job.Id);
        var input = new List<JobItem> { item };
        var staging = new JobItemRepository(db).AddRangeStagedAsync(input, timeout.Token);
        Exception? primary = null;
        try
        {
            var first = await Task.WhenAny(gate.Arrived, staging).WaitAsync(timeout.Token);
            if (first == staging)
                await staging;
            gate.Arrived.IsCompleted.ShouldBeTrue();
            input.Clear();
            input.Add(Item(Guid.NewGuid()));
            gate.Release();
            await staging.WaitAsync(timeout.Token);
            await boundary.CommitAsync(timeout.Token);
            (await db.JobItems.SingleAsync(timeout.Token)).Id.ShouldBe(item.Id);
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            gate.Release();
            await PostgreSqlLockQueue.CancelAndJoinAsync(timeout, primary, staging);
        }
    }

    private static JobItem Item(Guid jobId) => JobItem.ForRom(jobId, "a.rom", 7,
        RomIngestOutcome.Rejected, null, null, null, "policy", Now);

    private static async Task<(JobExecutionClaim<UploadJob> Claim, ManualTimeProvider Clock)> SeedClaimAsync(PostgreSqlTestDatabase database)
    {
        var clock = new ManualTimeProvider(Now);
        var job = UploadJob.Create("source.zip", Now.AddMinutes(-1));
        job.Start("original", Now.AddMinutes(-1));
        job.BeginClassification();
        job.SetDiscoveredFiles(0, 3);
        job.BeginDatIngestion();
        job.BeginRomIngestion();
        await using var db = database.CreateContext();
        db.Add(UploadJobEntity.FromDomain(job, Now));
        await db.SaveChangesAsync();
        // End committed fixture setup before the real claim maps its checkpoint entity.
        db.ChangeTracker.Clear();
        var claims = new ClaimedJobRepository<UploadJob, UploadJobRepository>(new(db, clock), db, clock, new());
        return ((await claims.TryClaimExecutionAsync(job.Id, "execution")).ShouldNotBeNull(), clock);
    }

    private sealed class PauseJournalLookup : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _paused;
        public Task Arrived => _arrived.Task;
        public void Release() => _release.TrySetResult();
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!_paused && command.CommandText.Contains("\"JobItems\"", StringComparison.Ordinal))
            {
                _paused = true;
                _arrived.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class FailJournalFlushOnce(PostgreSqlTestDatabase database, bool cancelled) : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }
        public Exception Failure { get; } = cancelled ? new OperationCanceledException("Injected journal cancellation.")
            : new IOException("Injected journal failure.");
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (!Fired && eventData.Context!.ChangeTracker.Entries<JobItemEntity>().Any())
            {
                Fired = true;
                (await eventData.Context.Set<JobItemEntity>().CountAsync(cancellationToken)).ShouldBe(1);
                await using var observer = database.CreateContext();
                (await observer.JobItems.CountAsync(cancellationToken)).ShouldBe(0);
                throw Failure;
            }
            return result;
        }
    }
}
