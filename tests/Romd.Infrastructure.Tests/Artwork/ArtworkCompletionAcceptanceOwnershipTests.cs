using System.Data;
using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Admin.Application.Artwork;
using Romd.Admin.Application.Artwork.Commands.QueueArtworkCompletion;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Ids;
using Romd.Contracts.Management.Artwork;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Artwork;
using Romd.Persistence.Entities;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Artwork;

public sealed class ArtworkCompletionAcceptanceOwnershipTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddTicks(1234567);
    private readonly PostgreSqlTestDatabase _database = PostgreSqlTestDatabase.Create();
    private readonly Guid _actor = Guid.NewGuid();
    private readonly Guid _parent = Guid.NewGuid();
    private readonly ManualTimeProvider _clock = new(Now);

    public async Task InitializeAsync()
    {
        await using var db = _database.CreateContext();
        db.Users.Add(new Romd.Persistence.Identity.RomdUser { Id = _actor, UserName = "curator", CreatedAt = Now });
        db.Platforms.Add(new PlatformEntity
        {
            Id = 7,
            Name = "Completion",
            ShortName = "completion",
            CanonicalKey = "completion",
            BaseName = "Completion",
            BaseCompactLabel = "Completion",
            BuiltInVersion = 1,
            Ownership = Romd.Domain.ReferenceData.ReferenceOwnership.Romd
        });
        db.Titles.AddRange(Enumerable.Range(1, 2).Select(id => new TitleEntity
        {
            Id = id,
            PlatformId = 7,
            Name = $"Title {id}",
            NormalizedName = $"title {id}",
            EnrichmentStatus = "None",
            CreatedAt = Now
        }));
        var parent = ArtworkCompletionJobEntity.FromDomain(ArtworkCompletionJob.Create(_parent, 1, _actor, Now.AddDays(-1), "Enrich"));
        parent.ScopeJson = ArtworkCompletionIntent.Signature(Selected(_parent, [1]));
        parent.Phase = "CompletedWithErrors";
        parent.Processed = 1;
        db.Add(parent);
        db.Add(new ArtworkCompletionItemEntity
        {
            JobId = _parent,
            TitleId = 1,
            TitleName = "Original title name",
            CompletedAt = Now.AddDays(-1),
            MetadataOutcome = MetadataExecutionOutcome.Failed
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    [Theory]
    [InlineData(FailurePoint.JobFlush, false)]
    [InlineData(FailurePoint.JobFlush, true)]
    [InlineData(FailurePoint.Items, false)]
    [InlineData(FailurePoint.Items, true)]
    [InlineData(FailurePoint.RetryAttachment, false)]
    [InlineData(FailurePoint.RetryAttachment, true)]
    [InlineData(FailurePoint.BeforeCommit, false)]
    [InlineData(FailurePoint.BeforeCommit, true)]
    public async Task HandleAsync_FaultOrCancellationAfterActualAcceptanceWrites_RollsBackJobDispatchItemsAndRetry(FailurePoint point, bool cancel)
    {
        await using var db = _database.CreateContext();
        using var cancellation = new CancellationTokenSource();
        var primary = new IOException("Injected completion acceptance fault.");
        var request = Retry();
        async Task InterruptAsync(CancellationToken ct)
        {
            await AssertBaselineAsync();
            (await db.Jobs.CountAsync(ct)).ShouldBe(2);
            (await db.JobDispatches.CountAsync(ct)).ShouldBe(1);
            if (point is FailurePoint.Items or FailurePoint.RetryAttachment or FailurePoint.BeforeCommit)
                (await db.Set<ArtworkCompletionItemEntity>().CountAsync(ct)).ShouldBe(2);
            if (point is FailurePoint.RetryAttachment or FailurePoint.BeforeCommit)
                (await db.Set<ArtworkCompletionItemEntity>().SingleAsync(item => item.JobId == _parent, ct)).FollowUpJobId.ShouldBe(request.RequestId);
            if (cancel)
            { await cancellation.CancelAsync(); ct.ThrowIfCancellationRequested(); }
            throw primary;
        }
        var work = new ObservedUnitOfWork(new EfUnitOfWork(db))
        {
            AfterFlush = point == FailurePoint.JobFlush ? InterruptAsync : null,
            BeforeCommit = point == FailurePoint.BeforeCommit ? InterruptAsync : null
        };
        var adapter = new ObservedAcceptance(Store(db))
        {
            AfterItems = point == FailurePoint.Items ? InterruptAsync : null,
            AfterRetry = point == FailurePoint.RetryAttachment ? InterruptAsync : null
        };
        Exception? failure = null;
        try
        { await Handler(adapter, work).HandleAsync(new(request, _actor), cancellation.Token); }
        catch (Exception error) { failure = error; }

        if (cancel)
            failure.ShouldBeOfType<OperationCanceledException>().CancellationToken.ShouldBe(cancellation.Token);
        else
            failure.ShouldBeSameAs(primary);
        work.Committed.ShouldBeFalse();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await db.SaveChangesAsync();
        await AssertBaselineAsync();
    }

    [Fact]
    public async Task HandleAsync_CommittedTransactionCleanupFails_ReturnsDurableIdentityAndWholeRetryGraph()
    {
        await using var db = _database.CreateContext();
        var work = new ObservedUnitOfWork(new EfUnitOfWork(db)) { CleanupFailure = new IOException("after commit") };
        var request = Retry();

        var result = await Handler(Store(db), work).HandleAsync(new(request, _actor));

        result.Value.ShouldBe(request.RequestId);
        work.Committed.ShouldBeTrue();
        await AssertAcceptedRetryAsync(request.RequestId);
    }

    [Fact]
    public async Task HandleAsync_CommitStillPending_DoesNotReturnOrExposeDispatchAndItems()
    {
        await using var db = _database.CreateContext();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var committing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new ObservedUnitOfWork(new EfUnitOfWork(db))
        {
            BeforeCommit = async ct => { committing.SetResult(); await release.Task.WaitAsync(ct); }
        };
        var request = Retry();
        var operation = Handler(Store(db), work).HandleAsync(new(request, _actor), cancellation.Token);
        Exception? failure = null;
        try
        {
            await committing.Task.WaitAsync(cancellation.Token);
            operation.IsCompleted.ShouldBeFalse();
            await AssertBaselineAsync();
            release.SetResult();
            (await operation).Value.ShouldBe(request.RequestId);
        }
        catch (Exception error) { failure = error; throw; }
        finally { await PostgreSqlLockQueue.CancelAndJoinAsync(cancellation, failure, operation); }
        await AssertAcceptedRetryAsync(request.RequestId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StagedReceipt_RequiresFlushAndOriginalAdapterTransaction_ExpiresAfterSettlement(bool commit)
    {
        await using var db = _database.CreateContext();
        var store = Store(db);
        var job = ArtworkCompletionJob.Create(Guid.NewGuid(), 1, _actor, Now, "Enrich");
        var signature = ArtworkCompletionIntent.Signature(Selected(job.Id, [1]));
        ArtworkCompletionPlannedItem[] items = [new(1, "Snapshot name", false, null)];
        Should.Throw<InvalidOperationException>(() => store.AddJobStaged(job, signature, items));
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await using (var wrongIsolation = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead))
            Should.Throw<InvalidOperationException>(() => store.AddJobStaged(job, signature, items));
        var work = new EfUnitOfWork(db);
        IStagedArtworkCompletionAcceptance receipt;
        await using (var transaction = await work.BeginTransactionAsync())
        {
            receipt = store.AddJobStaged(job, signature, items);
            items[0] = new(2, "Caller mutation", true, Guid.NewGuid());
            Should.Throw<InvalidOperationException>(() => receipt.ReadAfterFlush());
            await using (var another = _database.CreateContext())
                await new EfUnitOfWork(another).FlushAsync();
            Should.Throw<InvalidOperationException>(() => receipt.ReadAfterFlush());
            await work.FlushAsync();
            receipt.ReadAfterFlush().ShouldBe(job.Id);
            await Should.ThrowAsync<InvalidOperationException>(() => Store(db).InsertItemsStagedAsync(receipt, Now, default));
            await using (var another = _database.CreateContext())
            await using (var boundary = await new EfUnitOfWork(another).BeginTransactionAsync())
                await Should.ThrowAsync<InvalidOperationException>(() => Store(another).InsertItemsStagedAsync(receipt, Now, default));
            await store.InsertItemsStagedAsync(receipt, Now, default);
            var item = await db.Set<ArtworkCompletionItemEntity>().SingleAsync(row => row.JobId == job.Id);
            (item.TitleId, item.TitleName, item.RequireTracked, item.FollowUpJobId).ShouldBe((1, "Snapshot name", false, null));
            if (commit)
                await transaction.CommitAsync();
            else
                await transaction.RollbackAsync();
        }
        Should.Throw<InvalidOperationException>(() => receipt.ReadAfterFlush());
        await using var replacement = await work.BeginTransactionAsync();
        Should.Throw<InvalidOperationException>(() => receipt.ReadAfterFlush());
        await Should.ThrowAsync<InvalidOperationException>(() => store.InsertItemsStagedAsync(receipt, Now, default));
        await using var observer = _database.CreateContext();
        (await observer.Jobs.CountAsync()).ShouldBe(commit ? 2 : 1);
        (await observer.JobDispatches.CountAsync()).ShouldBe(commit ? 1 : 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StagedReceipt_DetachedJobOrDispatch_RejectsBeforeItemInsertion(bool detachDispatch)
    {
        await using var db = _database.CreateContext();
        var work = new EfUnitOfWork(db);
        await using var transaction = await work.BeginTransactionAsync();
        var store = Store(db);
        var job = ArtworkCompletionJob.Create(Guid.NewGuid(), 1, _actor, Now, "Enrich");
        var receipt = store.AddJobStaged(job, ArtworkCompletionIntent.Signature(Selected(job.Id, [1])), [new(1, "Title 1", false, null)]);
        await work.FlushAsync();
        if (detachDispatch)
            db.ChangeTracker.Entries<JobDispatchEntity>().Single(entry => entry.Entity.JobId == job.Id).State = EntityState.Detached;
        else
            db.ChangeTracker.Entries<ArtworkCompletionJobEntity>().Single(entry => entry.Entity.Id == job.Id).State = EntityState.Detached;

        Should.Throw<InvalidOperationException>(() => receipt.ReadAfterFlush());
        await Should.ThrowAsync<InvalidOperationException>(() => store.InsertItemsStagedAsync(receipt, Now, default));
        await transaction.RollbackAsync();
        await AssertBaselineAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleAsync_ConcurrentSameRequest_WaitsForRequestWinnerThenReplaysOrRejectsChangedIntent(bool changed)
    {
        await using var first = _database.CreateContext();
        await using var second = _database.CreateContext();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new ObservedUnitOfWork(new EfUnitOfWork(first))
        { AfterFlush = async ct => { flushed.SetResult(); await release.Task.WaitAsync(ct); } };
        var request = Selected(Guid.NewGuid(), [1]);
        var operations = new List<Task>();
        Exception? failure = null;
        try
        {
            var winner = Handler(Store(first), work).HandleAsync(new(request, _actor), cancellation.Token);
            operations.Add(winner);
            await flushed.Task.WaitAsync(cancellation.Token);
            var (pending, waited) = await _database.StartWaitingForALockAsync(second,
                ct => Handler(Store(second), new EfUnitOfWork(second)).HandleAsync(new(changed ? request with { Mode = "Artwork" } : request, _actor), ct),
                cancellation.Token);
            operations.Add(pending);
            waited.ShouldBeTrue();
            release.SetResult();
            (await winner).Value.ShouldBe(request.RequestId);
            if (changed)
                (await pending).FirstError.Code.ShouldBe("Artwork.RequestConflict");
            else
                (await pending).Value.ShouldBe(request.RequestId);
        }
        catch (Exception error) { failure = error; throw; }
        finally { await PostgreSqlLockQueue.CancelAndJoinAsync(cancellation, failure, operations.ToArray()); }
        await using var observer = _database.CreateContext();
        (await observer.Jobs.CountAsync()).ShouldBe(2);
        (await observer.JobDispatches.SingleAsync()).JobId.ShouldBe(request.RequestId);
        (await observer.Set<ArtworkCompletionItemEntity>().CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task HandleAsync_CancelledWhileWaitingForOwnerLock_LeavesNoAcceptance()
    {
        await using var hold = _database.CreateContext();
        await using var boundary = await new EfUnitOfWork(hold).BeginTransactionAsync();
        await Store(hold).LockOwnersAsync(default);
        await using var db = _database.CreateContext();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var request = Selected(Guid.NewGuid(), [1]);
        var (pending, waited) = await _database.StartWaitingForALockAsync(db,
            ct => Handler(Store(db), new EfUnitOfWork(db)).HandleAsync(new(request, _actor), ct), cancellation.Token);
        Exception? failure = null;
        try
        {
            waited.ShouldBeTrue();
            await cancellation.CancelAsync();
            Exception? cancelled = null;
            try
            { await pending; }
            catch (OperationCanceledException error) { cancelled = error; }
            cancelled.ShouldBeOfType<OperationCanceledException>().CancellationToken.ShouldBe(cancellation.Token);
            db.ChangeTracker.Entries().ShouldBeEmpty();
        }
        catch (Exception error) { failure = error; throw; }
        finally { await PostgreSqlLockQueue.CancelAndJoinAsync(cancellation, failure, pending); }
        await AssertBaselineAsync();
    }

    [Fact]
    public async Task HandleAsync_ReplayBehindHeldOwnerLock_CompletesWithoutTakingThatLock()
    {
        var request = Selected(Guid.NewGuid(), [1]);
        await using (var first = _database.CreateContext())
            (await Handler(Store(first), new EfUnitOfWork(first)).HandleAsync(new(request, _actor))).Value.ShouldBe(request.RequestId);
        await using var hold = _database.CreateContext();
        await using var transaction = await new EfUnitOfWork(hold).BeginTransactionAsync();
        await Store(hold).LockOwnersAsync(default);
        await using var replay = _database.CreateContext();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var operation = Handler(Store(replay), new EfUnitOfWork(replay)).HandleAsync(new(request, _actor), cancellation.Token);
        Exception? failure = null;
        try
        { (await operation.WaitAsync(cancellation.Token)).Value.ShouldBe(request.RequestId); }
        catch (Exception error) { failure = error; throw; }
        finally { await PostgreSqlLockQueue.CancelAndJoinAsync(cancellation, failure, operation); }
    }

    [Fact]
    public async Task HandleAsync_OverlappingRequestWaitsForOwnerCommit_SharesOnlyDurableCompatibleWork()
    {
        await using var first = _database.CreateContext();
        await using var second = _database.CreateContext();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new ObservedUnitOfWork(new EfUnitOfWork(first))
        {
            AfterFlush = async ct => { flushed.SetResult(); await release.Task.WaitAsync(ct); }
        };
        var request = Selected(Guid.NewGuid(), [1]);
        var next = Selected(Guid.NewGuid(), [1, 2]);
        var operations = new List<Task>();
        Exception? failure = null;
        try
        {
            var original = Handler(Store(first), work).HandleAsync(new(request, _actor), cancellation.Token);
            operations.Add(original);
            await flushed.Task.WaitAsync(cancellation.Token);
            var (pending, waited) = await _database.StartWaitingForALockAsync(second,
                ct => Handler(Store(second), new EfUnitOfWork(second)).HandleAsync(new(next, _actor), ct), cancellation.Token);
            operations.Add(pending);
            waited.ShouldBeTrue();
            await AssertBaselineAsync();
            release.SetResult();
            (await original).Value.ShouldBe(request.RequestId);
            (await pending).Value.ShouldBe(next.RequestId);
        }
        catch (Exception error) { failure = error; throw; }
        finally { await PostgreSqlLockQueue.CancelAndJoinAsync(cancellation, failure, operations.ToArray()); }
        await using var observer = _database.CreateContext();
        var shared = await observer.Set<ArtworkCompletionItemEntity>().SingleAsync(item => item.JobId == next.RequestId && item.TitleId == 1);
        (shared.FollowUpJobId, shared.CompletedAt).ShouldBe(((Guid?)request.RequestId, (DateTimeOffset?)DateTimeOffset.FromUnixTimeMilliseconds(Now.ToUnixTimeMilliseconds())));
        var job = await observer.Set<ArtworkCompletionJobEntity>().SingleAsync(row => row.Id == next.RequestId);
        (job.Total, job.Processed).ShouldBe((2, 1));
        (await observer.JobDispatches.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task HandleAsync_DynamicScopeWaitsForOwnerLock_SeesCurrentTrackingWhileReplayKeepsOriginalSnapshot()
    {
        await using (var seed = _database.CreateContext())
        { seed.TrackedTitles.Add(new TrackedTitleEntity { TitleId = 1, CreatedAt = Now }); await seed.SaveChangesAsync(); }
        await using var hold = _database.CreateContext();
        await using var boundary = await new EfUnitOfWork(hold).BeginTransactionAsync();
        await Store(hold).LockOwnersAsync(default);
        await using var db = _database.CreateContext();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var request = new ArtworkCompletionRequest(Guid.NewGuid(), Scope: "AllTracked", Mode: "Enrich");
        var (pending, waited) = await _database.StartWaitingForALockAsync(db,
            ct => Handler(Store(db), new EfUnitOfWork(db)).HandleAsync(new(request, _actor), ct), cancellation.Token);
        Exception? failure = null;
        try
        {
            waited.ShouldBeTrue();
            await using (var changed = _database.CreateContext())
            { changed.TrackedTitles.Add(new TrackedTitleEntity { TitleId = 2, CreatedAt = Now }); await changed.SaveChangesAsync(); }
            await boundary.CommitAsync(cancellation.Token);
            (await pending).Value.ShouldBe(request.RequestId);
        }
        catch (Exception error) { failure = error; throw; }
        finally { await PostgreSqlLockQueue.CancelAndJoinAsync(cancellation, failure, pending); }
        await using (var changed = _database.CreateContext())
            await changed.TrackedTitles.Where(row => row.TitleId == 2).ExecuteDeleteAsync();
        var store = Store(db);
        var handler = Handler(store, new EfUnitOfWork(db));
        (await handler.HandleAsync(new(request, _actor))).Value.ShouldBe(request.RequestId);
        var next = request with { RequestId = Guid.NewGuid() };
        (await handler.HandleAsync(new(next, _actor))).Value.ShouldBe(next.RequestId);
        (await db.Set<ArtworkCompletionJobEntity>().SingleAsync(row => row.Id == request.RequestId)).Total.ShouldBe(2);
        (await db.Set<ArtworkCompletionJobEntity>().SingleAsync(row => row.Id == next.RequestId)).Total.ShouldBe(1);
    }

    [Fact]
    public async Task HandleAsync_UnrelatedTrackedStaging_RemainsTrackedAndCanBeEditedAfterAcceptance()
    {
        await using var db = _database.CreateContext();
        var title = await db.Titles.AsTracking().SingleAsync(row => row.Id == 2);
        title.Description = "Before acceptance";
        var work = new EfUnitOfWork(db);

        (await Handler(Store(db), work).HandleAsync(new(Selected(Guid.NewGuid(), [1]), _actor))).IsError.ShouldBeFalse();

        db.Entry(title).State.ShouldBe(EntityState.Unchanged);
        title.Description = "After acceptance";
        await using (var next = await work.BeginTransactionAsync())
            await next.CommitAsync();
        await using var observer = _database.CreateContext();
        (await observer.Titles.SingleAsync(row => row.Id == 2)).Description.ShouldBe("After acceptance");
    }

    private ArtworkCompletionStore Store(RomdDbContext db) => new(db, _clock);
    private QueueArtworkCompletionCommandHandler Handler(IArtworkCompletionAcceptance acceptance, IUnitOfWork work) =>
        new(acceptance, work, _clock, NullLogger<QueueArtworkCompletionCommandHandler>.Instance);
    private ArtworkCompletionRequest Retry() => new(Guid.NewGuid(), Scope: "Retry", RetryJobId: _parent, RetryStatus: "Failed");
    private static ArtworkCompletionRequest Selected(Guid id, int[] titles) => new(id, titles.Select(IdCoder.Encode).ToArray(), Mode: "Enrich");

    private async Task AssertBaselineAsync()
    {
        await using var observer = _database.CreateContext();
        (await observer.Jobs.CountAsync()).ShouldBe(1);
        (await observer.JobDispatches.CountAsync()).ShouldBe(0);
        var item = await observer.Set<ArtworkCompletionItemEntity>().SingleAsync();
        item.JobId.ShouldBe(_parent);
        item.FollowUpJobId.ShouldBeNull();
    }
    private async Task AssertAcceptedRetryAsync(Guid id)
    {
        await using var observer = _database.CreateContext();
        (await observer.Jobs.CountAsync()).ShouldBe(2);
        (await observer.JobDispatches.SingleAsync()).JobId.ShouldBe(id);
        var child = await observer.Set<ArtworkCompletionJobEntity>().SingleAsync(row => row.Id == id);
        child.CreatedByUserId.ShouldBe(_actor);
        (child.Total, child.Processed).ShouldBe((1, 0));
        (await observer.Set<ArtworkCompletionItemEntity>().SingleAsync(item => item.JobId == _parent)).FollowUpJobId.ShouldBe(id);
        (await observer.Set<ArtworkCompletionItemEntity>().SingleAsync(item => item.JobId == id)).CompletedAt.ShouldBeNull();
    }

    public enum FailurePoint { JobFlush, Items, RetryAttachment, BeforeCommit }
    private sealed class ObservedUnitOfWork(IUnitOfWork inner) : IUnitOfWork
    {
        public Func<CancellationToken, Task>? AfterFlush { get; init; }
        public Func<CancellationToken, Task>? BeforeCommit { get; init; }
        public Exception? CleanupFailure { get; init; }
        public bool Committed { get; private set; }
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public async Task FlushAsync(CancellationToken cancellationToken = default)
        {
            await inner.FlushAsync(cancellationToken);
            if (AfterFlush is not null)
                await AfterFlush(cancellationToken);
        }
        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new Transaction(this, await inner.BeginTransactionAsync(cancellationToken));
        private sealed class Transaction(ObservedUnitOfWork owner, ITransaction inner) : ITransaction
        {
            public async Task CommitAsync(CancellationToken cancellationToken = default)
            {
                if (owner.BeforeCommit is not null)
                    await owner.BeforeCommit(cancellationToken);
                await inner.CommitAsync(cancellationToken);
                owner.Committed = true;
            }
            public Task RollbackAsync(CancellationToken cancellationToken = default) => inner.RollbackAsync(cancellationToken);
            public async ValueTask DisposeAsync()
            {
                await inner.DisposeAsync();
                if (owner.CleanupFailure is { } error)
                    throw error;
            }
        }
    }
    private sealed class ObservedAcceptance(IArtworkCompletionAcceptance inner) : IArtworkCompletionAcceptance
    {
        public Func<CancellationToken, Task>? AfterItems { get; init; }
        public Func<CancellationToken, Task>? AfterRetry { get; init; }
        public Task<string?> ReadRetryModeAsync(Guid id, CancellationToken ct) => inner.ReadRetryModeAsync(id, ct);
        public Task LockRequestAsync(Guid id, CancellationToken ct) => inner.LockRequestAsync(id, ct);
        public Task<ArtworkCompletionRequestIdentity?> FindRequestAsync(Guid id, CancellationToken ct) => inner.FindRequestAsync(id, ct);
        public Task LockOwnersAsync(CancellationToken ct) => inner.LockOwnersAsync(ct);
        public Task<Guid?> FindActiveSelectedAsync(string signature, CancellationToken ct) => inner.FindActiveSelectedAsync(signature, ct);
        public Task<ErrorOr<ArtworkCompletionScopeSnapshot>> SnapshotScopeAsync(ArtworkCompletionRequest request, CancellationToken ct) => inner.SnapshotScopeAsync(request, ct);
        public Task<IReadOnlyList<ArtworkCompletionOwner>> ReadOwnersAsync(IReadOnlyList<int> ids, CancellationToken ct) => inner.ReadOwnersAsync(ids, ct);
        public IStagedArtworkCompletionAcceptance AddJobStaged(ArtworkCompletionJob job, string signature, IReadOnlyList<ArtworkCompletionPlannedItem> items) => inner.AddJobStaged(job, signature, items);
        public async Task InsertItemsStagedAsync(IStagedArtworkCompletionAcceptance receipt, DateTimeOffset at, CancellationToken ct)
        { await inner.InsertItemsStagedAsync(receipt, at, ct); if (AfterItems is not null) await AfterItems(ct); }
        public async Task AttachRetryStagedAsync(IStagedArtworkCompletionAcceptance receipt, Guid id, CancellationToken ct)
        { await inner.AttachRetryStagedAsync(receipt, id, ct); if (AfterRetry is not null) await AfterRetry(ct); }
    }
}
