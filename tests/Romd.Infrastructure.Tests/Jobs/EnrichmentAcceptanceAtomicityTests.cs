using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Ingestion.Jobs;
using Romd.Admin.Application.Ingestion.Jobs.Commands.RetryFailedItems;
using Romd.Admin.Application.Titles.Commands.QueueBulkEnrichmentJob;
using Romd.Admin.Application.Titles.Commands.QueueTitleEnrichmentJob;
using Romd.Domain.Catalog;
using Romd.Domain.Jobs;
using Romd.Domain.ReferenceData;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Identity;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class EnrichmentAcceptanceAtomicityTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-04T12:00:00.123456Z");
    private static readonly DateTimeOffset PersistedNow = DateTimeOffset.FromUnixTimeMilliseconds(Now.ToUnixTimeMilliseconds());
    private readonly PostgreSqlTestDatabase database = PostgreSqlTestDatabase.Create();
    private readonly Guid actor = Guid.NewGuid();
    private readonly Guid otherActor = Guid.NewGuid();

    public EnrichmentAcceptanceAtomicityTests()
    {
        using var db = database.CreateContext();
        db.Platforms.AddRange(Platform(3), Platform(4), Platform(7));
        db.Titles.AddRange(Title(7, 3), Title(8, 4), Title(9, 3));
        db.Users.AddRange(new RomdUser { Id = actor, UserName = "creator" }, new RomdUser { Id = otherActor, UserName = "other" });
        db.SaveChanges();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AcceptancePort_WithoutCallerTransaction_RejectsBeforeWriting(bool bulk, bool stage)
    {
        await using var db = database.CreateContext();

        var error = await CaptureAsync(() => stage ? StageAsync(db, NewJob(bulk)) : LockAsync(db, bulk));

        error.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe(bulk
            ? "Bulk enrichment acceptance requires a caller-owned transaction."
            : "Enrichment acceptance requires a caller-owned transaction.");
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await AssertEmptyAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddStaged_CallerFlushAndCommit_PublishesJobAndDispatchTogether(bool bulk)
    {
        await using var db = database.CreateContext();
        var unit = new EfUnitOfWork(db);
        await using var transaction = await unit.BeginTransactionAsync();
        await LockAsync(db, bulk);
        var job = NewJob(bulk);

        await StageAsync(db, job);

        db.Entry(db.Jobs.Local.Single()).State.ShouldBe(EntityState.Added);
        (await db.Jobs.CountAsync()).ShouldBe(0);
        (await db.JobDispatches.CountAsync()).ShouldBe(0);
        await unit.FlushAsync();
        (await db.Jobs.CountAsync()).ShouldBe(1);
        (await db.JobDispatches.CountAsync()).ShouldBe(1);
        await AssertEmptyAsync();
        await transaction.CommitAsync();
        await AssertAcceptedAsync(bulk, job.Id, actor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_NewRequest_CommitsCapturedJobAndDurableDispatch(bool bulk)
    {
        await using var db = database.CreateContext();
        var unit = new RecordingUnitOfWork(new EfUnitOfWork(db));

        var result = await HandleAsync(db, bulk, unit);

        result.IsError.ShouldBeFalse();
        unit.CommitCount.ShouldBe(1);
        var id = (await db.Jobs.SingleAsync()).Id;
        if (bulk)
            result.Value.ShouldBe(id);
        await AssertAcceptedAsync(bulk, id, actor);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Handle_SuccessfulCoalescing_RollsBackUnrelatedStagingAndAmbientFenceTouch(bool bulk, bool enriching)
    {
        var existing = NewJob(bulk, scope: EnrichmentScope.Tracked);
        if (enriching)
            existing.Start("existing", Now);
        var parent = UploadJobEntity.FromDomain(UploadJob.Create("parent.zip", Now.AddMinutes(-1)), Now.AddMinutes(-1));
        parent.Phase = nameof(UploadPhase.IngestingRoms);
        parent.StartedAt = Now.AddMinutes(-1);
        parent.ExecutionFenceToken = Guid.NewGuid();
        parent.ExecutionLeaseExpiresAtUtc = Now.AddMinutes(5);
        await using (var seed = database.CreateContext())
        {
            seed.Add(Entity(existing));
            seed.Add(parent);
            await seed.SaveChangesAsync();
        }
        await using var db = database.CreateContext();
        (await db.Titles.AsTracking().SingleAsync(title => title.Id == 7)).Name = "Must not be committed";
        var unit = new RecordingUnitOfWork(new EfUnitOfWork(db));
        using var fence = JobExecutionFenceScope.Enter(parent.Id, parent.ExecutionFenceToken.Value, new FixedClock());

        var result = await HandleAsync(db, bulk, unit, createdBy: otherActor);

        result.IsError.ShouldBeFalse();
        if (bulk)
            result.Value.ShouldBe(existing.Id);
        unit.CommitCount.ShouldBe(0);
        db.Database.CurrentTransaction.ShouldBeNull();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await using var read = database.CreateContext();
        (await read.Titles.SingleAsync(title => title.Id == 7)).Name.ShouldBe("Current title 7");
        (await read.Jobs.SingleAsync(job => job.Id == parent.Id)).UpdatedAt.ShouldBe(PersistedNow.AddMinutes(-1));
        (await read.Jobs.CountAsync()).ShouldBe(2);
        (await read.JobDispatches.CountAsync()).ShouldBe(enriching ? 0 : 1);
        if (bulk)
        {
            var row = await read.Set<BulkEnrichmentJobEntity>().SingleAsync();
            row.CreatedByUserId.ShouldBe(actor);
            row.ToDomain().Scope.ShouldBe(EnrichmentScope.Tracked);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Handle_FaultOrCancellationAfterRealFlush_RollsBackJobAndDispatch(bool bulk, bool cancel)
    {
        await using var db = database.CreateContext();
        using var cancellation = new CancellationTokenSource();
        var unit = new RecordingUnitOfWork(new EfUnitOfWork(db)) { FailOnCommit = 1, Cancellation = cancel ? cancellation : null };

        var error = await CaptureAsync(() => HandleAsync(db, bulk, unit, cancellation.Token));

        if (cancel)
            error.ShouldBeAssignableTo<OperationCanceledException>().ShouldNotBeNull().CancellationToken.ShouldBe(cancellation.Token);
        else
            error.ShouldBeSameAs(unit.Fault);
        unit.Flushed.ShouldBeTrue();
        db.Database.CurrentTransaction.ShouldBeNull();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await AssertEmptyAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_CleanupFaultAfterRealCommit_ReturnsDurableAcceptance(bool bulk)
    {
        await using var db = database.CreateContext();
        var unit = new RecordingUnitOfWork(new EfUnitOfWork(db)) { FailCleanupAfterCommit = true };

        var result = await HandleAsync(db, bulk, unit);

        result.IsError.ShouldBeFalse();
        var id = (await db.Jobs.SingleAsync()).Id;
        if (bulk)
            result.Value.ShouldBe(id);
        await AssertAcceptedAsync(bulk, id, actor);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task Handle_FailedOrLostCommitAcknowledgementWithCleanupFault_PreservesPrimaryAndNeverRetries(bool bulk, bool afterCommit, bool cancel)
    {
        await using var db = database.CreateContext();
        using var cancellation = new CancellationTokenSource();
        Exception cause = cancel ? new OperationCanceledException("primary acceptance cancellation", cancellation.Token)
            : new IOException("primary acceptance acknowledgement failure");
        var unit = new RecordingUnitOfWork(new EfUnitOfWork(db))
        {
            FailOnCommit = 1,
            FailAfterActualCommit = afterCommit,
            FailCleanup = true,
            Fault = cause
        };

        var error = await CaptureAsync(() => HandleAsync(db, bulk, unit, cancellation.Token));

        error.ShouldBeSameAs(cause);
        if (cancel)
            error.ShouldBeOfType<OperationCanceledException>().CancellationToken.ShouldBe(cancellation.Token);
        unit.BeginCount.ShouldBe(1);
        unit.CommitCount.ShouldBe(1);
        unit.DisposeCount.ShouldBe(1);
        unit.Flushed.ShouldBeTrue();
        db.Database.CurrentTransaction.ShouldBeNull();
        if (afterCommit)
        {
            await using var read = database.CreateContext();
            var id = (await read.Jobs.SingleAsync()).Id;
            await AssertAcceptedAsync(bulk, id, actor);
        }
        else
        {
            db.ChangeTracker.Entries().ShouldBeEmpty();
            await AssertEmptyAsync();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Handle_ConcurrentSameKey_CommitCoalescesAndRollbackAllowsWaitingRequest(bool bulk, bool commitHolder)
    {
        await using var holder = database.CreateContext();
        await using var contender = database.CreateContext();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var transaction = await new EfUnitOfWork(holder).BeginTransactionAsync(cancellation.Token);
        await LockAsync(holder, bulk, cancellation.Token);
        var first = NewJob(bulk, scope: EnrichmentScope.Tracked);
        await StageAsync(holder, first, cancellation.Token);
        var unit = new RecordingUnitOfWork(new EfUnitOfWork(contender));
        var (pending, waited) = await database.StartWaitingForALockAsync(contender,
            token => HandleAsync(contender, bulk, unit, token, otherActor), cancellation.Token);
        Exception? failure = null;
        try
        {
            waited.ShouldBeTrue();
            if (commitHolder)
                await transaction.CommitAsync(cancellation.Token);
            else
                await transaction.RollbackAsync(cancellation.Token);
            var result = await pending;
            result.IsError.ShouldBeFalse();
            unit.CommitCount.ShouldBe(commitHolder ? 0 : 1);
            await using var read = database.CreateContext();
            var row = (await read.Jobs.ToListAsync()).ShouldHaveSingleItem();
            (await read.JobDispatches.CountAsync()).ShouldBe(1);
            if (commitHolder)
            {
                row.Id.ShouldBe(first.Id);
                if (bulk)
                {
                    result.Value.ShouldBe(first.Id);
                    row.CreatedByUserId.ShouldBe(actor);
                    (await read.Set<BulkEnrichmentJobEntity>().SingleAsync()).ToDomain().Scope.ShouldBe(EnrichmentScope.Tracked);
                }
            }
            else
            {
                row.Id.ShouldNotBe(first.Id);
                if (bulk)
                    row.CreatedByUserId.ShouldBe(otherActor);
            }
        }
        catch (Exception error) { failure = error; throw; }
        finally { await PostgreSqlLockQueue.CancelAndJoinAsync(cancellation, failure, pending); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_DifferentKey_DoesNotWaitForUnrelatedAcceptance(bool bulk)
    {
        await using var holder = database.CreateContext();
        await using var contender = database.CreateContext();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var transaction = await new EfUnitOfWork(holder).BeginTransactionAsync(cancellation.Token);
        await LockAsync(holder, bulk, cancellation.Token);
        var (pending, waited) = await database.StartWaitingForALockAsync(contender,
            token => HandleAsync(contender, bulk, new EfUnitOfWork(contender), token, titleId: 8, platformId: 4), cancellation.Token);
        Exception? failure = null;
        try
        {
            waited.ShouldBeFalse();
            (await pending).IsError.ShouldBeFalse();
        }
        catch (Exception error) { failure = error; throw; }
        finally { await PostgreSqlLockQueue.CancelAndJoinAsync(cancellation, failure, pending); }
    }

    [Fact]
    public async Task Handle_SeparateFamiliesWithSameNumericKey_RemainIndependent()
    {
        await using var holder = database.CreateContext();
        await using var contender = database.CreateContext();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var transaction = await new EfUnitOfWork(holder).BeginTransactionAsync(cancellation.Token);
        await LockAsync(holder, bulk: false, cancellation.Token);
        var (pending, waited) = await database.StartWaitingForALockAsync(contender,
            token => HandleAsync(contender, bulk: true, new EfUnitOfWork(contender), token, platformId: 7), cancellation.Token);
        Exception? failure = null;
        try
        {
            waited.ShouldBeFalse();
            (await pending).IsError.ShouldBeFalse();
        }
        catch (Exception error) { failure = error; throw; }
        finally { await PostgreSqlLockQueue.CancelAndJoinAsync(cancellation, failure, pending); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_CancelledWhileQueuedForLock_LeavesNoPartialDemand(bool bulk)
    {
        await using var holder = database.CreateContext();
        await using var contender = database.CreateContext();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var transaction = await new EfUnitOfWork(holder).BeginTransactionAsync(cancellation.Token);
        await LockAsync(holder, bulk, cancellation.Token);
        var (pending, waited) = await database.StartWaitingForALockAsync(contender,
            token => HandleAsync(contender, bulk, new EfUnitOfWork(contender), token), cancellation.Token);
        Exception? failure = null;
        try
        {
            waited.ShouldBeTrue();
            await cancellation.CancelAsync();
            var error = await CaptureAsync(() => pending);
            error.ShouldBeAssignableTo<OperationCanceledException>().ShouldNotBeNull().CancellationToken.ShouldBe(cancellation.Token);
            contender.Database.CurrentTransaction.ShouldBeNull();
            contender.ChangeTracker.Entries().ShouldBeEmpty();
            await AssertEmptyAsync();
        }
        catch (Exception error) { failure = error; throw; }
        finally { await PostgreSqlLockQueue.CancelAndJoinAsync(cancellation, failure, pending); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryFailed_LaterAcceptanceFails_PreservesEarlierCommitAndOriginalHistory(bool cancel)
    {
        var source = await SeedFailedBulkAsync();
        await using var db = database.CreateContext();
        using var cancellation = new CancellationTokenSource();
        var unit = new RecordingUnitOfWork(new EfUnitOfWork(db)) { FailOnCommit = 2, Cancellation = cancel ? cancellation : null };
        var handler = new RetryFailedItemsCommandHandler(new JobRepository(db, new FixedClock()), SingleHandler(db, unit));

        var error = await CaptureAsync(() => handler.HandleAsync(new(source.Id), cancellation.Token));

        if (cancel)
            error.ShouldBeAssignableTo<OperationCanceledException>().ShouldNotBeNull().CancellationToken.ShouldBe(cancellation.Token);
        else
            error.ShouldBeSameAs(unit.Fault);
        unit.BeginCount.ShouldBe(2);
        await using var read = database.CreateContext();
        var retry = (await read.Set<EnrichmentJobEntity>().ToListAsync()).ShouldHaveSingleItem();
        retry.TitleId.ShouldBe(7);
        retry.SourceFilename.ShouldBe("Captured error 7");
        retry.CreatedByUserId.ShouldBeNull();
        (await read.JobDispatches.CountAsync()).ShouldBe(1);
        (await read.Jobs.CountAsync()).ShouldBe(2);
        await AssertSourceAndTitlesAsync(read, source);
    }

    [Fact]
    public async Task RetryFailed_PreexistingTitleCoalesces_CountsSelectedTitlesWithoutChangingHistoryOrMetadata()
    {
        var source = await SeedFailedBulkAsync();
        await using var db = database.CreateContext();
        await using (var transaction = await new EfUnitOfWork(db).BeginTransactionAsync())
        {
            await StageAsync(db, NewJob(bulk: false));
            await transaction.CommitAsync();
        }
        var unit = new RecordingUnitOfWork(new EfUnitOfWork(db));
        var handler = new RetryFailedItemsCommandHandler(new JobRepository(db, new FixedClock()), SingleHandler(db, unit));

        var result = await handler.HandleAsync(new(source.Id));

        result.Value.Requeued.ShouldBe(3);
        unit.CommitCount.ShouldBe(2);
        await using var read = database.CreateContext();
        (await read.Set<EnrichmentJobEntity>().CountAsync()).ShouldBe(3);
        (await read.JobDispatches.CountAsync()).ShouldBe(3);
        await AssertSourceAndTitlesAsync(read, source);
    }

    private async Task<BulkEnrichmentJobEntity> SeedFailedBulkAsync()
    {
        var job = BulkEnrichmentJob.Create(3, "system-3", Now, EnrichmentScope.All, actor);
        job.Start("source", Now);
        job.SetTotalTitles(3);
        foreach (int id in new[] { 7, 8, 9 })
            job.RecordFailed(id, $"Captured error {id}", "provider failed", JobErrorReason.ProviderError, Now);
        job.Complete(Now);
        var entity = BulkEnrichmentJobEntity.FromDomain(job, Now);
        await using var seed = database.CreateContext();
        seed.Add(entity);
        await seed.SaveChangesAsync();
        return entity;
    }

    private static async Task AssertSourceAndTitlesAsync(RomdDbContext read, BulkEnrichmentJobEntity source)
    {
        var original = await read.Set<BulkEnrichmentJobEntity>().SingleAsync();
        original.Id.ShouldBe(source.Id);
        original.Phase.ShouldBe(source.Phase);
        original.ErrorsJson.ShouldBe(source.ErrorsJson);
        original.CreatedByUserId.ShouldBe(source.CreatedByUserId);
        (await read.Titles.ToListAsync()).ShouldAllBe(title => title.EnrichmentStatus == "None" && title.Name == $"Current title {title.Id}");
    }

    private static PlatformEntity Platform(int id) => new()
    {
        Id = id,
        Name = $"System {id}",
        BaseName = $"System {id}",
        BaseCompactLabel = $"System {id}",
        CanonicalKey = $"system-{id}",
        ShortName = $"system-{id}",
        Ownership = ReferenceOwnership.Romd,
        BuiltInVersion = 1,
        CreatedAt = Now
    };
    private static TitleEntity Title(int id, int platform) => new()
    {
        Id = id,
        Name = $"Current title {id}",
        NormalizedName = $"current title {id}",
        PlatformId = platform,
        EnrichmentStatus = "None",
        CreatedAt = Now
    };
    private Job NewJob(bool bulk, EnrichmentScope scope = EnrichmentScope.All) => bulk
        ? BulkEnrichmentJob.Create(3, "system-3", Now, scope, actor)
        : EnrichmentJob.Create("Captured retry name", 7, 3, Now);
    private static JobEntity Entity(Job job) => job switch
    {
        EnrichmentJob single => EnrichmentJobEntity.FromDomain(single, Now),
        BulkEnrichmentJob bulk => BulkEnrichmentJobEntity.FromDomain(bulk, Now),
        _ => throw new ArgumentOutOfRangeException(nameof(job))
    };
    private static Task StageAsync(RomdDbContext db, Job job, CancellationToken ct = default) => job switch
    {
        EnrichmentJob single => new EnrichmentJobRepository(db, new FixedClock()).AddStagedAsync(single, ct),
        BulkEnrichmentJob bulk => new BulkEnrichmentJobRepository(db, new FixedClock()).AddStagedAsync(bulk, ct),
        _ => throw new ArgumentOutOfRangeException(nameof(job))
    };
    private static async Task LockAsync(RomdDbContext db, bool bulk, CancellationToken ct = default)
    {
        if (bulk)
            await new BulkEnrichmentJobRepository(db, new FixedClock()).LockAndGetPendingForPlatformStagedAsync(3, ct);
        else
            await new EnrichmentJobRepository(db, new FixedClock()).LockAndHasPendingForTitleStagedAsync(7, ct);
    }
    private static QueueTitleEnrichmentJobCommandHandler SingleHandler(RomdDbContext db, IUnitOfWork unit) =>
        new(new EnrichmentJobRepository(db, new FixedClock()), unit, new FixedClock(), NullLogger<QueueTitleEnrichmentJobCommandHandler>.Instance);
    private async Task<ErrorOr<object>> HandleAsync(RomdDbContext db, bool bulk, IUnitOfWork unit, CancellationToken ct = default,
        Guid? createdBy = null, int titleId = 7, int platformId = 3)
    {
        if (bulk)
        {
            var handler = new QueueBulkEnrichmentJobCommandHandler(new BulkEnrichmentJobRepository(db, new FixedClock()),
                new PlatformRepository(db), unit, new FixedClock(), NullLogger<QueueBulkEnrichmentJobCommandHandler>.Instance);
            var result = await handler.HandleAsync(new(platformId, EnrichmentScope.All, createdBy ?? actor), ct);
            return result.IsError ? result.Errors : ErrorOrFactory.From<object>(result.Value);
        }
        var single = await SingleHandler(db, unit).HandleAsync(new(titleId, "Captured retry name", platformId), ct);
        return single.IsError ? single.Errors : ErrorOrFactory.From<object>(single.Value);
    }
    private async Task AssertEmptyAsync()
    {
        await using var read = database.CreateContext();
        (await read.Jobs.CountAsync()).ShouldBe(0);
        (await read.JobDispatches.CountAsync()).ShouldBe(0);
    }
    private async Task AssertAcceptedAsync(bool bulk, Guid id, Guid createdBy)
    {
        await using var read = database.CreateContext();
        var row = (await read.Jobs.ToListAsync()).ShouldHaveSingleItem();
        row.Id.ShouldBe(id);
        row.PlatformId.ShouldBe(3);
        row.CreatedAt.ShouldBe(PersistedNow);
        row.SourceFilename.ShouldBe(bulk ? "system-3" : "Captured retry name");
        row.CreatedByUserId.ShouldBe(bulk ? createdBy : null);
        if (bulk)
            (await read.Set<BulkEnrichmentJobEntity>().SingleAsync()).ToDomain().Scope.ShouldBe(EnrichmentScope.All);
        else
            (await read.Set<EnrichmentJobEntity>().SingleAsync()).TitleId.ShouldBe(7);
        var dispatch = (await read.JobDispatches.ToListAsync()).ShouldHaveSingleItem();
        dispatch.JobId.ShouldBe(id);
        dispatch.JobType.ShouldBe(bulk ? "bulk_enrichment" : "enrichment");
        dispatch.CreatedAtUtc.ShouldBe(PersistedNow);
        dispatch.AvailableAtUtc.ShouldBe(PersistedNow);
    }
    private static async Task<Exception?> CaptureAsync(Func<Task> operation)
    {
        try
        { await operation(); return null; }
        catch (Exception error) { return error; }
    }
    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }

    private sealed class RecordingUnitOfWork(IUnitOfWork inner) : IUnitOfWork
    {
        public int BeginCount { get; private set; }
        public int CommitCount { get; private set; }
        public int DisposeCount { get; private set; }
        public int FailOnCommit { get; init; }
        public bool FailCleanupAfterCommit { get; init; }
        public bool FailCleanup { get; init; }
        public bool FailAfterActualCommit { get; init; }
        public CancellationTokenSource? Cancellation { get; init; }
        public bool Flushed { get; private set; }
        public Exception Fault { get; init; } = new IOException("injected acceptance failure after flush");
        public Task FlushAsync(CancellationToken cancellationToken = default) => inner.FlushAsync(cancellationToken);
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        {
            BeginCount++;
            return new Transaction(await inner.BeginTransactionAsync(cancellationToken), this);
        }
        private async Task CommitAsync(ITransaction transaction, CancellationToken ct)
        {
            CommitCount++;
            if (CommitCount == FailOnCommit)
            {
                if (FailAfterActualCommit)
                    await transaction.CommitAsync(ct);
                else
                    await inner.FlushAsync(ct);
                Flushed = true;
                if (Cancellation is not null)
                { await Cancellation.CancelAsync(); ct.ThrowIfCancellationRequested(); }
                throw Fault;
            }
            await transaction.CommitAsync(ct);
        }
        private sealed class Transaction(ITransaction innerTransaction, RecordingUnitOfWork owner) : ITransaction
        {
            private bool committed;
            public async Task CommitAsync(CancellationToken cancellationToken = default)
            {
                await owner.CommitAsync(innerTransaction, cancellationToken);
                committed = true;
            }
            public Task RollbackAsync(CancellationToken cancellationToken = default) => innerTransaction.RollbackAsync(cancellationToken);
            public async ValueTask DisposeAsync()
            {
                await innerTransaction.DisposeAsync();
                owner.DisposeCount++;
                if (owner.FailCleanup || (committed && owner.FailCleanupAfterCommit))
                    throw new IOException("injected cleanup failure after commit");
            }
        }
    }

    public void Dispose() => database.Dispose();
}
