using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Cqrs;
using Romd.Application.Common.Security;
using Romd.Application.Common.Security.Commands.StartAccountSession;
using Romd.Infrastructure;
using Romd.Persistence;
using Romd.Persistence.Identity;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Identity;

public sealed class AccountSessionCreationAtomicityTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-04T12:00:00.123456+05:30");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StageStart_WithoutTransaction_RejectsBeforeTracking(bool cancelled)
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        using var cancellation = new CancellationTokenSource();
        if (cancelled)
            cancellation.Cancel();
        var store = new AccountSessionStore(db, new Clock());
        if (cancelled)
            await Should.ThrowAsync<OperationCanceledException>(() => store.StageStartAsync(Facts(Guid.NewGuid()), cancellation.Token));
        else
            await Should.ThrowAsync<InvalidOperationException>(() => store.StageStartAsync(Facts(Guid.NewGuid())));
        db.ChangeTracker.Entries().ShouldBeEmpty();
        (await db.AccountSessions.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task StageStart_DoesNotFlushUnrelatedWork_AndCallerRollbackDiscardsBoth()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var user = await SeedUserAsync(db);
        await using var transaction = await new EfUnitOfWork(db).BeginTransactionAsync();
        user.UserName = "unrelated staged change";

        var session = await new AccountSessionStore(db, new Clock()).StageStartAsync(Facts(user.Id));

        session.CreatedAt.ShouldBe(Now);
        db.AccountSessions.Local.ShouldHaveSingleItem().Id.ShouldBe(session.Id);
        (await db.AccountSessions.CountAsync()).ShouldBe(0);
        (await db.Users.AsNoTracking().SingleAsync()).UserName.ShouldBe("session-owner");
        await transaction.RollbackAsync();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        (await db.AccountSessions.CountAsync()).ShouldBe(0);
        (await db.Users.SingleAsync()).UserName.ShouldBe("session-owner");
    }

    [Fact]
    public async Task Handle_RealCommit_PreservesFullPrecisionResponseAndUnixMillisecondRows()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var user = await SeedUserAsync(db);

        var result = await Handler(db).HandleAsync(new(user.Id, "captured-client", "  Device\n  ", "captured-stamp"));

        result.IsError.ShouldBeFalse();
        var session = result.Value;
        session.Device.ShouldBe("Device");
        session.CreatedAt.ShouldBe(Now);
        session.CreatedAt.Offset.ShouldBe(Now.Offset);
        session.LastUsedAt.ShouldBe(Now);
        session.ExpiresAt.ShouldBe(Now.AddDays(30));
        session.Status.ShouldBe("Current");
        await using var read = database.CreateContext();
        var row = await read.AccountSessions.SingleAsync();
        row.Id.ShouldBe(session.Id);
        row.UserId.ShouldBe(user.Id);
        row.ClientId.ShouldBe("captured-client");
        row.AccountStamp.ShouldBe("captured-stamp");
        row.CreatedAt.ShouldBe(UnixMs(Now));
        row.LastUsedAt.ShouldBe(UnixMs(Now));
        row.ExpiresAt.ShouldBe(UnixMs(Now.AddDays(30)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Handle_FaultOrCancellationAfterActualSqlFlush_RollsBackSession(bool cancel)
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var user = await SeedUserAsync(db);
        using var cancellation = new CancellationTokenSource();
        var fault = new IOException("injected session failure after SQL flush");
        var unit = new ObservedUnit(new EfUnitOfWork(db), async () =>
        {
            (await db.AccountSessions.CountAsync()).ShouldBe(1);
            await using var outside = database.CreateContext();
            (await outside.AccountSessions.CountAsync()).ShouldBe(0);
            if (cancel)
            { await cancellation.CancelAsync(); cancellation.Token.ThrowIfCancellationRequested(); }
            throw fault;
        });

        if (cancel)
            (await Should.ThrowAsync<OperationCanceledException>(() => Handler(db, unit).HandleAsync(Command(user.Id), cancellation.Token)))
            .CancellationToken.ShouldBe(cancellation.Token);
        else
            (await Should.ThrowAsync<IOException>(() => Handler(db, unit).HandleAsync(Command(user.Id)))).ShouldBeSameAs(fault);

        unit.Flushed.ShouldBeTrue();
        db.Database.CurrentTransaction.ShouldBeNull();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await using var read = database.CreateContext();
        (await read.AccountSessions.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task Handle_ActualCommitHeld_DoesNotReturnBeforeExternalVisibility()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var user = await SeedUserAsync(db);
        var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unit = new ObservedUnit(new EfUnitOfWork(db), async () => { flushed.SetResult(); await release.Task; });
        var pending = Handler(db, unit).HandleAsync(Command(user.Id));
        Exception? failure = null;
        try
        {
            await flushed.Task.WaitAsync(TimeSpan.FromSeconds(20));
            pending.IsCompleted.ShouldBeFalse();
            await using var outside = database.CreateContext();
            (await outside.AccountSessions.CountAsync()).ShouldBe(0);
            release.SetResult();
            var accepted = (await pending).Value;
            (await outside.AccountSessions.SingleAsync()).Id.ShouldBe(accepted.Id);
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            release.TrySetResult();
            try
            { await pending; }
            catch when (failure is not null) { }
        }
    }

    [Fact]
    public async Task Handle_CleanupFaultAfterActualCommit_ReturnsDurableSession()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var user = await SeedUserAsync(db);
        var unit = new ObservedUnit(new EfUnitOfWork(db), () => Task.CompletedTask, failCleanup: true);

        var result = await Handler(db, unit).HandleAsync(Command(user.Id));

        result.IsError.ShouldBeFalse();
        await using var read = database.CreateContext();
        (await read.AccountSessions.SingleAsync()).Id.ShouldBe(result.Value.Id);
    }

    [Theory]
    [InlineData("stamp", false)]
    [InlineData("stamp", true)]
    [InlineData("suspension", false)]
    [InlineData("suspension", true)]
    [InlineData("activation", false)]
    [InlineData("activation", true)]
    public async Task Handle_AccountChangesAfterCapturedPrincipal_PreservesInsertionButLaterValidationRejects(string change, bool afterFlush)
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var user = await SeedUserAsync(db);
        async Task ChangeAsync()
        {
            await using var competing = database.CreateContext();
            var query = competing.Users.Where(item => item.Id == user.Id);
            if (change == "stamp")
                await query.ExecuteUpdateAsync(set => set.SetProperty(item => item.SecurityStamp, "new-stamp"));
            else if (change == "suspension")
                await query.ExecuteUpdateAsync(set => set.SetProperty(item => item.IsSuspended, true));
            else
                await query.ExecuteUpdateAsync(set => set.SetProperty(item => item.RequiresActivation, true));
        }
        if (!afterFlush)
            await ChangeAsync();
        var unit = new ObservedUnit(new EfUnitOfWork(db), afterFlush ? ChangeAsync : () => Task.CompletedTask);

        var result = await Handler(db, unit).HandleAsync(Command(user.Id));

        result.IsError.ShouldBeFalse();
        result.Value.Status.ShouldBe("Current"); // Captured insertion is deliberately not a new eligibility CAS.
        await using var read = database.CreateContext();
        (await read.AccountSessions.SingleAsync()).AccountStamp.ShouldBe("captured-stamp");
        var authority = new AccountSessionStore(read, new Clock());
        (await authority.ValidateAsync(user.Id, result.Value.Id, "captured-stamp", default)).ShouldBeNull();
        (await authority.ValidateAsync(user.Id, result.Value.Id, "new-stamp", default)).ShouldBeNull();
        (await authority.ListAsync(user.Id, default)).ShouldHaveSingleItem().Status.ShouldBe("Invalidated");
    }

    [Fact]
    public async Task Handle_AllSessionRevocationRacesFlush_OldStampSessionNeverValidates()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var user = await SeedUserAsync(db);
        var unit = new ObservedUnit(new EfUnitOfWork(db), async () =>
        {
            await using var competing = database.CreateContext();
            await using var revocation = await new EfUnitOfWork(competing).BeginTransactionAsync();
            await new AccountSessionStore(competing, new Clock()).RevokeAsync(user.Id, null, null, user.Id, "All sessions revoked", default);
            await competing.Users.Where(item => item.Id == user.Id).ExecuteUpdateAsync(set => set.SetProperty(item => item.SecurityStamp, "revoked-stamp"));
            await revocation.CommitAsync();
        });

        var accepted = (await Handler(db, unit).HandleAsync(Command(user.Id))).Value;

        await using var read = database.CreateContext();
        (await read.AccountSessions.SingleAsync()).AccountStamp.ShouldBe("captured-stamp");
        (await new AccountSessionStore(read, new Clock()).ValidateAsync(user.Id, accepted.Id, "captured-stamp", default)).ShouldBeNull();
    }

    [Fact]
    public async Task Handle_TwoIndependentSignIns_CreatesDistinctSessionsWithoutDeduplication()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var first = database.CreateContext();
        var user = await SeedUserAsync(first);
        await using var second = database.CreateContext();
        var sessions = await Task.WhenAll(Handler(first).HandleAsync(Command(user.Id)), Handler(second).HandleAsync(Command(user.Id)));
        sessions[0].Value.Id.ShouldNotBe(sessions[1].Value.Id);
        (await first.AccountSessions.CountAsync()).ShouldBe(2);
    }

    [Fact]
    public async Task Handle_StoreAndBoundaryFromDifferentContexts_RejectsInsteadOfLeakingStandaloneSave()
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using var boundaryContext = database.CreateContext();
        await using var storeContext = database.CreateContext();
        var user = await SeedUserAsync(boundaryContext);
        var handler = new StartAccountSessionCommandHandler(new AccountSessionStore(storeContext, new Clock()),
            new AccountSessionTransaction(new EfUnitOfWork(boundaryContext), NullLogger<AccountSessionTransaction>.Instance), new Clock());

        await Should.ThrowAsync<InvalidOperationException>(() => handler.HandleAsync(Command(user.Id)));

        boundaryContext.Database.CurrentTransaction.ShouldBeNull();
        storeContext.ChangeTracker.Entries().ShouldBeEmpty();
        (await storeContext.AccountSessions.CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("consumer")]
    [InlineData("testing")]
    public async Task Composition_StoreBoundaryAndCommandShareOneScopedContext_AndCommitTogether(string composition)
    {
        using var database = PostgreSqlTestDatabase.Create();
        await using (var seed = database.CreateContext())
            await SeedUserAsync(seed);
        var services = Services(composition, database.ConnectionString);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<RomdDbContext>();
        var user = await db.Users.SingleAsync();
        var canonical = sp.GetRequiredService<AccountSessionStore>();
        sp.GetRequiredService<IAccountSessions>().ShouldBeSameAs(canonical);
        sp.GetRequiredService<IAccountSessionCreationStore>().ShouldBeSameAs(canonical);
        var handler = sp.GetRequiredService<ICommandHandler<StartAccountSessionCommand, AccountSession>>();

        var result = await handler.HandleAsync(Command(user.Id));

        result.IsError.ShouldBeFalse();
        db.AccountSessions.Local.ShouldHaveSingleItem().Id.ShouldBe(result.Value.Id);
        db.Database.CurrentTransaction.ShouldBeNull();
        await using var read = database.CreateContext();
        (await read.AccountSessions.SingleAsync()).Id.ShouldBe(result.Value.Id);
        if (composition == "consumer")
            sp.GetService<IUnitOfWork>().ShouldBeNull();
    }

    internal static ServiceCollection Services(string composition, string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new Clock());
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ConnectionStrings:Romd"] = connectionString }).Build());
        if (composition == "consumer")
            services.AddRomdConsumerPersistence().AddRomdConsumerApplicationHandlers();
        else if (composition == "admin")
            services.AddRomdPersistence().AddRomdAdminApplicationHandlers();
        else
            services.AddRomdTestingPersistence(connectionString).AddRomdApplicationHandlers();
        return services;
    }
    private static StartAccountSessionCommandHandler Handler(RomdDbContext db, IUnitOfWork? unit = null) => new(
        new AccountSessionStore(db, new Clock()), new AccountSessionTransaction(unit ?? new EfUnitOfWork(db), NullLogger<AccountSessionTransaction>.Instance), new Clock());
    private static StartAccountSessionCommand Command(Guid user) => new(user, "client", "Device", "captured-stamp");
    private static AccountSessionCreationFacts Facts(Guid user) => new(Guid.NewGuid(), user, "client", "Device", "captured-stamp", Now, Now.AddDays(30));
    private static async Task<RomdUser> SeedUserAsync(RomdDbContext db)
    {
        var user = new RomdUser { UserName = "session-owner", SecurityStamp = "captured-stamp" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }
    private static DateTimeOffset UnixMs(DateTimeOffset value) => DateTimeOffset.FromUnixTimeMilliseconds(value.ToUnixTimeMilliseconds());
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }

    private sealed class ObservedUnit(IUnitOfWork inner, Func<Task> afterFlush, bool failCleanup = false) : IUnitOfWork
    {
        public bool Flushed { get; private set; }
        public Task FlushAsync(CancellationToken ct = default) => inner.FlushAsync(ct);
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public async Task<ITransaction> BeginTransactionAsync(CancellationToken ct = default) => new Transaction(await inner.BeginTransactionAsync(ct), this);
        private bool FailCleanup => failCleanup;
        private async Task CommitAsync(ITransaction transaction, CancellationToken ct)
        {
            await inner.FlushAsync(ct);
            Flushed = true;
            await afterFlush();
            await transaction.CommitAsync(ct);
        }
        private sealed class Transaction(ITransaction transaction, ObservedUnit owner) : ITransaction
        {
            private bool committed;
            public async Task CommitAsync(CancellationToken ct = default)
            {
                await owner.CommitAsync(transaction, ct);
                committed = true;
            }
            public Task RollbackAsync(CancellationToken ct = default) => transaction.RollbackAsync(ct);
            public async ValueTask DisposeAsync()
            {
                await transaction.DisposeAsync();
                if (committed && owner.FailCleanup)
                    throw new IOException("injected session cleanup failure");
            }
        }
    }
}
