using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Admin.Application.Source.Dat;
using Romd.Admin.Application.Source.Dat.Commands.CheckDatSubscription;
using Romd.Admin.Application.Source.Dat.Commands.StopDatSubscription;
using Romd.Admin.Application.Storage.Files;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Subscriptions;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Subscriptions;

public sealed class StopDatSubscriptionAtomicityTests
{
    private const string Catalog = "test/catalog";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopStaged_WithoutTransaction_RejectsBeforeTracking(bool cancelled)
    {
        using var database = PostgreSqlTestDatabase.Create();
        int id = await SeedAsync(database);
        await using var db = database.CreateContext();
        using var cancellation = new CancellationTokenSource();
        if (cancelled)
            cancellation.Cancel();

        if (cancelled)
            await Should.ThrowAsync<OperationCanceledException>(() => Store(db).StopStagedAsync(id, cancellation.Token));
        else
            await Should.ThrowAsync<InvalidOperationException>(() => Store(db).StopStagedAsync(id));

        db.ChangeTracker.Entries().ShouldBeEmpty();
        (await db.DatSubscriptions.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task StopStaged_StagesDeletionWithoutFlushingUnrelatedWork_AndOwnerRollbackRestoresSubscription()
    {
        using var database = PostgreSqlTestDatabase.Create();
        int id = await SeedAsync(database);
        await using var db = database.CreateContext();
        var unit = new EfUnitOfWork(db);
        await using var transaction = await unit.BeginTransactionAsync();
        var unrelated = new PlatformEntity { Name = "pending platform", ShortName = "pending" };
        db.Platforms.Add(unrelated);

        (await Store(db).StopStagedAsync(id)).IsError.ShouldBeFalse();

        db.Entry(unrelated).State.ShouldBe(EntityState.Added);
        db.ChangeTracker.Entries<DatSubscriptionEntity>().Single().State.ShouldBe(EntityState.Deleted);
        (await db.DatSubscriptions.CountAsync()).ShouldBe(1);
        (await db.Platforms.CountAsync()).ShouldBe(0);
        await unit.FlushAsync();
        (await db.DatSubscriptions.CountAsync()).ShouldBe(0);
        await using (var observer = database.CreateContext())
            (await observer.DatSubscriptions.CountAsync()).ShouldBe(1);
        await transaction.RollbackAsync();

        db.ChangeTracker.Entries().ShouldBeEmpty();
        (await db.DatSubscriptions.CountAsync()).ShouldBe(1);
        (await db.Platforms.CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_FaultOrCancellationAfterRealFlush_RollsBackDeletion(bool cancel)
    {
        using var database = PostgreSqlTestDatabase.Create();
        int id = await SeedAsync(database);
        await using var db = database.CreateContext();
        using var cancellation = new CancellationTokenSource();
        var unit = new ObservedUnitOfWork(new EfUnitOfWork(db), () =>
        {
            if (cancel)
            {
                cancellation.Cancel();
                cancellation.Token.ThrowIfCancellationRequested();
            }
            return Task.FromException(new IOException("injected failure after deletion flush"));
        });

        if (cancel)
            await Should.ThrowAsync<OperationCanceledException>(() => Handler(db, unit).HandleAsync(new(id), cancellation.Token));
        else
            await Should.ThrowAsync<IOException>(() => Handler(db, unit).HandleAsync(new(id), cancellation.Token));

        unit.Flushed.ShouldBeTrue();
        db.Database.CurrentTransaction.ShouldBeNull();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await using var observer = database.CreateContext();
        (await observer.DatSubscriptions.SingleAsync()).Id.ShouldBe(id);
        (await observer.JobDispatches.CountAsync()).ShouldBe(0);
        (await observer.AdminRealtimeOutboxEvents.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Handle_ConcurrentCheckWhileDeletionIsFlushed_ReturnsBusyThenNotFoundWithoutResurrection()
    {
        using var database = PostgreSqlTestDatabase.Create();
        int id = await SeedAsync(database);
        await using var db = database.CreateContext();
        await using var checking = database.CreateContext();
        var unit = new ObservedUnitOfWork(new EfUnitOfWork(db), async () =>
        {
            (await Check(checking).HandleAsync(new(id))).FirstError.Code.ShouldBe("DatEnrollment.Busy");
            (await checking.DatSubscriptions.CountAsync()).ShouldBe(1);
        });

        var result = await Handler(db, unit).HandleAsync(new(id));

        result.IsError.ShouldBeFalse();
        unit.Flushed.ShouldBeTrue();
        (await Check(checking).HandleAsync(new(id))).FirstError.Code.ShouldBe("DatEnrollment.NotFound");
        (await checking.DatSubscriptions.CountAsync()).ShouldBe(0);
        (await checking.Jobs.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Handle_CatalogLockHeld_ReturnsBusyWithoutDeleting_AndRetryCommits()
    {
        using var database = PostgreSqlTestDatabase.Create();
        int id = await SeedAsync(database);
        await using var holder = database.CreateContext();
        await using var db = database.CreateContext();
        var handler = Handler(db);
        await using (var transaction = await holder.Database.BeginTransactionAsync())
        {
            (await holder.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock(146, hashtext({Catalog})) AS \"Value\"").SingleAsync()).ShouldBeTrue();

            (await handler.HandleAsync(new(id))).FirstError.Code.ShouldBe("DatEnrollment.Busy");

            db.ChangeTracker.Entries().ShouldBeEmpty();
            db.Database.CurrentTransaction.ShouldBeNull();
            (await db.DatSubscriptions.CountAsync()).ShouldBe(1);
        }

        (await handler.HandleAsync(new(id))).IsError.ShouldBeFalse();
        (await db.DatSubscriptions.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Handle_DisposalFailsAfterCommit_ReturnsSuccessForDurableDeletion()
    {
        using var database = PostgreSqlTestDatabase.Create();
        int id = await SeedAsync(database);
        await using var db = database.CreateContext();
        var unit = new ObservedUnitOfWork(new EfUnitOfWork(db), () => Task.CompletedTask, failDisposal: true);

        (await Handler(db, unit).HandleAsync(new(id))).IsError.ShouldBeFalse();

        unit.Flushed.ShouldBeTrue();
        db.Database.CurrentTransaction.ShouldBeNull();
        await using var observer = database.CreateContext();
        (await observer.DatSubscriptions.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Handle_UnknownIdentity_ReturnsExistingNotFoundError()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();

        (await Handler(db).HandleAsync(new(int.MaxValue))).FirstError.Code.ShouldBe("DatEnrollment.NotFound");

        db.Database.CurrentTransaction.ShouldBeNull();
        db.ChangeTracker.Entries().ShouldBeEmpty();
    }

    private static async Task<int> SeedAsync(PostgreSqlTestDatabase database)
    {
        await using var db = database.CreateContext();
        var row = new DatSubscriptionEntity { CatalogId = Catalog };
        db.DatSubscriptions.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    private static DatCatalogEnrollmentService Store(RomdDbContext db)
    {
        var client = Substitute.For<ISignedDatCatalogClient>();
        client.Enabled.Returns(true);
        return new(db, client, Substitute.For<IDatRepository>(),
            Substitute.For<IFileStorageService>(), Substitute.For<IDatReplacementReview>(),
            TimeProvider.System);
    }

    private static CheckDatSubscriptionCommandHandler Check(RomdDbContext db)
    {
        var client = Substitute.For<ISignedDatCatalogClient>();
        client.Enabled.Returns(true);
        var store = Store(db);
        return new(store, store, Substitute.For<IDatRepository>(), client, Substitute.For<IDatReplacementReview>(),
            Substitute.For<IFileStorageService>(), new EfUnitOfWork(db), TimeProvider.System, NullLogger<CheckDatSubscriptionCommandHandler>.Instance);
    }

    private static StopDatSubscriptionCommandHandler Handler(RomdDbContext db, IUnitOfWork? unit = null) =>
        new(Store(db), unit ?? new EfUnitOfWork(db), NullLogger<StopDatSubscriptionCommandHandler>.Instance);

    private sealed class ObservedUnitOfWork(IUnitOfWork inner, Func<Task> afterFlush, bool failDisposal = false) : IUnitOfWork
    {
        private readonly IUnitOfWork _inner = inner;
        private readonly Func<Task> _afterFlush = afterFlush;
        private readonly bool _failDisposal = failDisposal;
        public bool Flushed { get; private set; }
        public Task FlushAsync(CancellationToken cancellationToken = default) => _inner.FlushAsync(cancellationToken);
        public TransactionConflict ConflictOf(Exception error) => _inner.ConflictOf(error);

        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new Transaction(await _inner.BeginTransactionAsync(cancellationToken), this);

        private sealed class Transaction(ITransaction transaction, ObservedUnitOfWork owner) : ITransaction
        {
            public async Task CommitAsync(CancellationToken cancellationToken = default)
            {
                await owner._inner.FlushAsync(cancellationToken);
                owner.Flushed = true;
                await owner._afterFlush();
                await transaction.CommitAsync(cancellationToken);
            }

            public Task RollbackAsync(CancellationToken cancellationToken = default) => transaction.RollbackAsync(cancellationToken);
            public async ValueTask DisposeAsync()
            {
                await transaction.DisposeAsync();
                if (owner._failDisposal)
                    throw new IOException("injected cleanup failure after commit");
            }
        }
    }
}
