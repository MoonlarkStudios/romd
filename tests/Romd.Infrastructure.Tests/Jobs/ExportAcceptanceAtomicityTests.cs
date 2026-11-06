using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Export;
using Romd.Admin.Application.Export.Commands.QueueLibraryExport;
using Romd.Admin.Application.Export.Queries.ResolveExportScope;
using Romd.Application.Common.Security;
using Romd.Domain.Identity;
using Romd.Domain.Jobs;
using Romd.Domain.Source.Rom;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Identity;
using Romd.Persistence.Repositories;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Jobs;

public sealed class ExportAcceptanceAtomicityTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-04T12:00:00.123456Z");
    private static readonly DateTimeOffset PersistedNow = DateTimeOffset.FromUnixTimeMilliseconds(Now.ToUnixTimeMilliseconds());
    private readonly PostgreSqlTestDatabase database = PostgreSqlTestDatabase.Create();
    private readonly Guid actor = Guid.NewGuid();
    private readonly Guid otherActor = Guid.NewGuid();

    public ExportAcceptanceAtomicityTests()
    {
        using var db = database.CreateContext();
        db.Libraries.Add(new LibraryEntity
        {
            Id = 7,
            Name = "Export library",
            ConfigurationJson = "{}",
            ConfigurationState = "Valid",
            MaterializationGeneration = 5,
            LastMaterializedAt = Now,
            CreatedAt = Now,
            UpdatedAt = Now
        });
        db.Users.AddRange(new RomdUser { Id = actor, UserName = "exporter", LibraryId = 7 },
            new RomdUser { Id = otherActor, UserName = "other-exporter" });
        db.SaveChanges();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcceptancePort_WithoutCallerTransaction_RejectsBeforeWritingOrLocking(bool create)
    {
        await using var db = database.CreateContext();
        var store = Store(db);

        var error = await Should.ThrowAsync<InvalidOperationException>(() => create
            ? store.AddStagedAsync(NewJob())
            : store.LockAndHasPendingStagedAsync());

        error.Message.ShouldBe("Export acceptance requires a caller-owned transaction.");
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await AssertEmptyAsync();
    }

    [Fact]
    public async Task AddStaged_CallerFlushesThenCommits_PublishesJobAndDispatchTogether()
    {
        await using var db = database.CreateContext();
        var unit = new EfUnitOfWork(db);
        await using var transaction = await unit.BeginTransactionAsync();
        var store = Store(db);
        (await store.LockAndHasPendingStagedAsync()).ShouldBeFalse();
        var job = NewJob();

        await store.AddStagedAsync(job);

        db.Entry(db.Jobs.Local.Single()).State.ShouldBe(EntityState.Added);
        (await db.Jobs.CountAsync()).ShouldBe(0);
        (await db.JobDispatches.CountAsync()).ShouldBe(0);
        await unit.FlushAsync();
        (await db.Jobs.CountAsync()).ShouldBe(1);
        (await db.JobDispatches.CountAsync()).ShouldBe(1);
        await AssertEmptyAsync();
        await transaction.CommitAsync();
        await AssertAcceptedAsync(job.Id, actor, library: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_AuthorizedScope_CommitsExactCreatorFormatScopeAndDispatch(bool library)
    {
        await using var db = database.CreateContext();

        var result = await Handler(db).HandleAsync(new(library ? 7 : null, RomDeliveryFormat.Chd));

        result.IsError.ShouldBeFalse();
        await AssertAcceptedAsync(result.Value, actor, library, "chd");
    }

    [Fact]
    public async Task Handle_PendingRejectionAfterCallerPreludeFlush_RollsBackStagedJobAndDispatch()
    {
        await using var db = database.CreateContext();
        var unit = new PreludeUnitOfWork(new EfUnitOfWork(db), Store(db), NewJob());

        var result = await Handler(db, unit).HandleAsync(new(null));

        result.FirstError.ShouldBe(ExportErrors.AlreadyInProgress());
        unit.Flushed.ShouldBeTrue();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        db.Database.CurrentTransaction.ShouldBeNull();
        await AssertEmptyAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_FaultOrCancellationAfterRealFlush_RollsBackJobAndDispatch(bool cancel)
    {
        await using var db = database.CreateContext();
        using var cancellation = new CancellationTokenSource();
        var unit = new FaultAfterFlushUnitOfWork(new EfUnitOfWork(db), cancellation, cancel);

        var error = await CaptureAsync(() => Handler(db, unit).HandleAsync(new(7, RomDeliveryFormat.Chd), cancellation.Token));

        if (cancel)
            error.ShouldBeAssignableTo<OperationCanceledException>().ShouldNotBeNull().CancellationToken.ShouldBe(cancellation.Token);
        else
            error.ShouldBeSameAs(unit.Fault);
        unit.Flushed.ShouldBeTrue();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        db.Database.CurrentTransaction.ShouldBeNull();
        await AssertEmptyAsync();
    }

    [Fact]
    public async Task Handle_CleanupFaultAfterRealCommit_ReturnsDurableIdentity()
    {
        await using var db = database.CreateContext();
        var unit = new CleanupFaultUnitOfWork(new EfUnitOfWork(db));

        var result = await Handler(db, unit).HandleAsync(new(7, RomDeliveryFormat.Rvz));

        result.IsError.ShouldBeFalse();
        db.Database.CurrentTransaction.ShouldBeNull();
        await AssertAcceptedAsync(result.Value, actor, library: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_ConcurrentScopesAndActors_UsesOneGlobalAcceptanceLock(bool commitHolder)
    {
        await using var holder = database.CreateContext();
        await using var contender = database.CreateContext();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var transaction = await new EfUnitOfWork(holder).BeginTransactionAsync(cancellation.Token);
        var store = Store(holder);
        (await store.LockAndHasPendingStagedAsync(cancellation.Token)).ShouldBeFalse();
        var first = NewJob();
        await store.AddStagedAsync(first, cancellation.Token);
        var (pending, waited) = await database.StartWaitingForALockAsync(contender,
            token => Handler(contender, createdBy: otherActor).HandleAsync(new(null, RomDeliveryFormat.Chd), token), cancellation.Token);
        Exception? failure = null;
        try
        {
            waited.ShouldBeTrue();
            if (commitHolder)
                await transaction.CommitAsync(cancellation.Token);
            else
                await transaction.RollbackAsync(cancellation.Token);
            var result = await pending;
            if (commitHolder)
            {
                result.FirstError.ShouldBe(ExportErrors.AlreadyInProgress());
                await AssertAcceptedAsync(first.Id, actor, library: true);
            }
            else
            {
                result.IsError.ShouldBeFalse();
                result.Value.ShouldNotBe(first.Id);
                await AssertAcceptedAsync(result.Value, otherActor, library: false, "chd");
            }
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally
        {
            await PostgreSqlLockQueue.CancelAndJoinAsync(cancellation, failure, pending);
        }
    }

    [Fact]
    public async Task Handle_CancelledWhileQueuedForGlobalLock_LeavesNoPartialAcceptance()
    {
        await using var holder = database.CreateContext();
        await using var contender = database.CreateContext();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var transaction = await new EfUnitOfWork(holder).BeginTransactionAsync(cancellation.Token);
        (await Store(holder).LockAndHasPendingStagedAsync(cancellation.Token)).ShouldBeFalse();
        var first = NewJob();
        await Store(holder).AddStagedAsync(first, cancellation.Token);
        var (pending, waited) = await database.StartWaitingForALockAsync(contender,
            token => Handler(contender).HandleAsync(new(null), token), cancellation.Token);
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
            await transaction.CommitAsync();
            await AssertAcceptedAsync(first.Id, actor, library: true);
        }
        catch (Exception error)
        {
            failure = error;
            throw;
        }
        finally
        {
            await PostgreSqlLockQueue.CancelAndJoinAsync(cancellation, failure, pending);
        }
    }

    [Theory]
    [InlineData("Pending", true)]
    [InlineData("Collecting", true)]
    [InlineData("Packaging", true)]
    [InlineData("Storing", true)]
    [InlineData("Completed", false)]
    [InlineData("CompletedWithErrors", false)]
    [InlineData("Failed", false)]
    [InlineData("Cancelled", false)]
    public async Task Handle_ExistingPhase_PreservesGlobalPendingDefinition(string phase, bool active)
    {
        await using var db = database.CreateContext();
        var existing = ExportJobEntity.FromDomain(NewJob(), Now);
        existing.Phase = phase;
        db.Add(existing);
        await db.SaveChangesAsync();

        var result = await Handler(db).HandleAsync(new(null));

        if (active)
            result.FirstError.ShouldBe(ExportErrors.AlreadyInProgress());
        else
            result.IsError.ShouldBeFalse();
        (await db.Jobs.CountAsync()).ShouldBe(active ? 1 : 2);
        (await db.JobDispatches.CountAsync()).ShouldBe(phase == "Pending" || !active ? 1 : 0);
    }

    private ExportJob NewJob() => ExportJob.CreateLibrary(7, 5, Now, actor, RomDeliveryFormat.Rvz);
    private static ExportJobRepository Store(RomdDbContext db) => new(db, new FixedClock());
    private QueueLibraryExportCommandHandler Handler(RomdDbContext db, IUnitOfWork? unit = null, Guid? createdBy = null)
    {
        var current = Substitute.For<ICurrentUser>();
        current.UserId.Returns(createdBy ?? actor);
        current.HasRole(RomdRoleType.Admin).Returns(true);
        return new(new ResolveExportScopeQueryHandler(current, new ExportAuthorizationReader(db)), current, Store(db),
            unit ?? new EfUnitOfWork(db), new FixedClock(), NullLogger<QueueLibraryExportCommandHandler>.Instance);
    }

    private async Task AssertEmptyAsync()
    {
        await using var read = database.CreateContext();
        (await read.Jobs.CountAsync()).ShouldBe(0);
        (await read.JobDispatches.CountAsync()).ShouldBe(0);
    }

    private async Task AssertAcceptedAsync(Guid id, Guid createdBy, bool library, string format = "rvz")
    {
        await using var read = database.CreateContext();
        var job = (await read.Jobs.OfType<ExportJobEntity>().ToListAsync()).ShouldHaveSingleItem();
        job.Id.ShouldBe(id);
        id.ShouldNotBe(Guid.Empty);
        job.CreatedByUserId.ShouldBe(createdBy);
        job.ExportScopeKind.ShouldBe(library ? "Library" : "AllCatalog");
        job.LibraryId.ShouldBe(library ? 7 : null);
        job.AuthorizedMaterializationGeneration.ShouldBe(library ? 5 : null);
        job.ExportFormat.ShouldBe(format);
        job.CreatedAt.ShouldBe(PersistedNow);
        var dispatch = (await read.JobDispatches.ToListAsync()).ShouldHaveSingleItem();
        dispatch.JobId.ShouldBe(id);
        dispatch.JobType.ShouldBe("export");
        dispatch.CreatedAtUtc.ShouldBe(PersistedNow);
        dispatch.AvailableAtUtc.ShouldBe(PersistedNow);
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> operation)
    {
        try
        { await operation(); return null; }
        catch (Exception error) { return error; }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class PreludeUnitOfWork(IUnitOfWork inner, IExportJobAcceptance acceptance, ExportJob job) : IUnitOfWork
    {
        public bool Flushed { get; private set; }
        public Task FlushAsync(CancellationToken cancellationToken = default) => inner.FlushAsync(cancellationToken);
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        {
            var transaction = await inner.BeginTransactionAsync(cancellationToken);
            try
            {
                await acceptance.AddStagedAsync(job, cancellationToken);
                await inner.FlushAsync(cancellationToken);
                Flushed = true;
                return transaction;
            }
            catch
            {
                await transaction.DisposeAsync();
                throw;
            }
        }
    }

    private sealed class FaultAfterFlushUnitOfWork(IUnitOfWork inner, CancellationTokenSource cancellation, bool cancel) : IUnitOfWork
    {
        public Exception Fault { get; } = new IOException("injected acceptance commit failure");
        public bool Flushed { get; private set; }
        public Task FlushAsync(CancellationToken cancellationToken = default) => inner.FlushAsync(cancellationToken);
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new FaultingTransaction(await inner.BeginTransactionAsync(cancellationToken), this);
        private async Task FailAsync(CancellationToken ct)
        {
            await inner.FlushAsync(ct);
            Flushed = true;
            if (cancel)
            {
                await cancellation.CancelAsync();
                ct.ThrowIfCancellationRequested();
            }
            throw Fault;
        }
        private sealed class FaultingTransaction(ITransaction innerTransaction, FaultAfterFlushUnitOfWork owner) : ITransaction
        {
            public Task CommitAsync(CancellationToken cancellationToken = default) => owner.FailAsync(cancellationToken);
            public Task RollbackAsync(CancellationToken cancellationToken = default) => innerTransaction.RollbackAsync(cancellationToken);
            public ValueTask DisposeAsync() => innerTransaction.DisposeAsync();
        }
    }

    private sealed class CleanupFaultUnitOfWork(IUnitOfWork inner) : IUnitOfWork
    {
        public Task FlushAsync(CancellationToken cancellationToken = default) => inner.FlushAsync(cancellationToken);
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new CleanupFaultTransaction(await inner.BeginTransactionAsync(cancellationToken));
        private sealed class CleanupFaultTransaction(ITransaction transaction) : ITransaction
        {
            public Task CommitAsync(CancellationToken cancellationToken = default) => transaction.CommitAsync(cancellationToken);
            public Task RollbackAsync(CancellationToken cancellationToken = default) => transaction.RollbackAsync(cancellationToken);
            public async ValueTask DisposeAsync()
            {
                await transaction.DisposeAsync();
                throw new IOException("injected cleanup failure after acceptance commit");
            }
        }
    }

    public void Dispose() => database.Dispose();
}
