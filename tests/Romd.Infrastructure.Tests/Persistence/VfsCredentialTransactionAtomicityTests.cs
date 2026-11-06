using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Security;
using Romd.Consumer.Application.Common.Persistence;
using Romd.Consumer.Application.Vfs;
using Romd.Consumer.Application.Vfs.Commands.CreateVfsConnection;
using Romd.Consumer.Application.Vfs.Commands.RevokeVfsConnection;
using Romd.Consumer.Application.Vfs.Commands.RotateVfsConnection;
using Romd.Domain.Vfs;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.Persistence.Identity;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class VfsCredentialTransactionAtomicityTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-04T12:34:56.123456Z");
    private readonly PostgreSqlTestDatabase database = PostgreSqlTestDatabase.Create();
    private readonly Guid userId = Guid.NewGuid();

    public VfsCredentialTransactionAtomicityTests()
    {
        using var db = database.CreateContext();
        db.Users.Add(new RomdUser { Id = userId, UserName = "owner", SecurityStamp = "current-stamp" });
        db.SaveChanges();
    }

    [Theory]
    [InlineData("create")]
    [InlineData("rotate")]
    [InlineData("revoke")]
    public async Task CredentialMutation_WithoutCallerTransaction_ThrowsBeforeWriting(string operation)
    {
        await using var db = CreateConsumerContext();
        var store = Store(db);

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => operation switch
        {
            "create" => store.CreateAsync(userId, "Deck", default),
            "rotate" => store.RotateAsync(userId, Guid.NewGuid(), default),
            "revoke" => (Task)store.RevokeAsync(userId, Guid.NewGuid(), default),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        });

        failure.Message.ShouldBe("VFS credential mutations require a caller-owned transaction.");
        db.ChangeTracker.Entries().ShouldBeEmpty();
        (await db.VfsConnections.CountAsync()).ShouldBe(0);
    }

    [Theory]
    [InlineData("create", "error")]
    [InlineData("create", "fault")]
    [InlineData("create", "cancel")]
    [InlineData("rotate", "error")]
    [InlineData("rotate", "fault")]
    [InlineData("rotate", "cancel")]
    [InlineData("revoke", "error")]
    [InlineData("revoke", "fault")]
    [InlineData("revoke", "cancel")]
    public async Task CredentialMutation_RejectedAfterFlush_RollsBackCredentialAndClearsStagedState(string operation, string failure)
    {
        var original = await SeedCredentialAsync();
        await using var db = CreateConsumerContext();
        using var cancellation = new CancellationTokenSource();
        var transaction = new RejectAfterFlush(db, Transaction(db), failure, cancellation);

        if (failure == "error")
        {
            var result = await HandleAsync(operation, db, transaction, original.Connection.Id, cancellation.Token);
            result.FirstError.ShouldBe(VfsConnectionErrors.LimitReached());
        }
        else
        {
            var error = await Record.ExceptionAsync(() => HandleAsync(operation, db, transaction, original.Connection.Id, cancellation.Token));
            if (failure == "cancel")
            {
                error.ShouldBeOfType<OperationCanceledException>().CancellationToken.ShouldBe(cancellation.Token);
            }
            else
            {
                error.ShouldBeSameAs(transaction.Fault);
            }
        }

        transaction.Flushed.ShouldBeTrue();
        db.Database.CurrentTransaction.ShouldBeNull();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await using var read = CreateConsumerContext();
        var row = (await read.VfsConnections.ToListAsync()).ShouldHaveSingleItem();
        row.Id.ShouldBe(original.Connection.Id);
        row.RevokedAt.ShouldBeNull();
        row.CredentialUpdatedAt.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(Now.ToUnixTimeMilliseconds()));
        (await Store(read).AuthenticateAsync(original.Connection.Username, original.Password, default)).ShouldNotBeNull();
        if (transaction.RejectedCredential is { } rejected)
        {
            (await Store(read).AuthenticateAsync(rejected.Connection.Username, rejected.Password, default)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task Create_StagedCredential_RemainsUnflushedAndInvisibleUntilCallerCommits()
    {
        await using var db = CreateConsumerContext();
        var unit = new EfUnitOfWork(db);
        await using var transaction = await unit.BeginTransactionAsync();

        var issued = (await Store(db).CreateAsync(userId, "Staged", default)).Value;

        db.Entry(db.VfsConnections.Local.Single()).State.ShouldBe(EntityState.Added);
        (await db.VfsConnections.CountAsync()).ShouldBe(0);
        await unit.FlushAsync();
        (await db.VfsConnections.CountAsync()).ShouldBe(1);
        await using var read = CreateConsumerContext();
        (await read.VfsConnections.CountAsync()).ShouldBe(0);
        await transaction.CommitAsync();
        (await read.VfsConnections.SingleAsync()).Id.ShouldBe(issued.Connection.Id);
    }

    [Theory]
    [InlineData("create", true)]
    [InlineData("create", false)]
    [InlineData("rotate", true)]
    [InlineData("rotate", false)]
    public async Task CredentialMutation_AccountDisabled_RechecksUserInsideTransaction(string operation, bool suspended)
    {
        var original = await SeedCredentialAsync();
        await using (var seed = database.CreateContext())
        {
            await seed.Users.Where(user => user.Id == userId).ExecuteUpdateAsync(setters => setters
                .SetProperty(user => user.IsSuspended, suspended)
                .SetProperty(user => user.RequiresActivation, !suspended));
        }
        await using var db = CreateConsumerContext();
        var before = await db.VfsConnections.SingleAsync();

        var result = await HandleAsync(operation, db, Transaction(db), original.Connection.Id, default);

        result.FirstError.ShouldBe(Romd.Consumer.Application.ConsumerErrors.CurrentUserRequired());
        db.Database.CurrentTransaction.ShouldBeNull();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        var after = (await db.VfsConnections.ToListAsync()).ShouldHaveSingleItem();
        after.PasswordVerifier.ShouldBe(before.PasswordVerifier);
        after.CredentialUpdatedAt.ShouldBe(before.CredentialUpdatedAt);
    }

    [Fact]
    public async Task Create_CleanupFailsAfterCommit_ReturnsUsableDurableCredential()
    {
        await using var db = CreateConsumerContext();
        var transaction = new ConsumerSecurityTransaction(new CleanupFaultUnitOfWork(new EfUnitOfWork(db)),
            NullLogger<ConsumerSecurityTransaction>.Instance);

        var result = await new CreateVfsConnectionCommandHandler(CurrentUser(), Store(db), transaction)
            .HandleAsync(new("Deck", VfsFormat.Original));

        result.IsError.ShouldBeFalse();
        result.Value.Connection.CreatedAt.ShouldBe(Now);
        result.Value.Connection.CredentialUpdatedAt.ShouldBe(Now);
        await using var read = CreateConsumerContext();
        var authenticated = await Store(read).AuthenticateAsync(result.Value.Connection.Username, result.Value.Password, default);
        authenticated.ShouldNotBeNull().Format.ShouldBe(VfsFormat.Original);
    }

    [Fact]
    public async Task Create_ConcurrentRequestsAt31Active_OnlyOneCommitsAndOtherRechecksLimit()
    {
        await SeedActiveConnectionsAsync(31);
        await using var holder = CreateConsumerContext();
        await using var contender = CreateConsumerContext();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var transaction = await new EfUnitOfWork(holder).BeginTransactionAsync(cancellation.Token);
        var first = await Store(holder).CreateAsync(userId, "First", cancellation.Token);
        first.IsError.ShouldBeFalse();
        var (pending, waited) = await database.StartWaitingForALockAsync(contender,
            token => new CreateVfsConnectionCommandHandler(CurrentUser(), Store(contender), Transaction(contender))
                .HandleAsync(new("Second"), token), cancellation.Token);
        Exception? failure = null;
        try
        {
            waited.ShouldBeTrue();
            await transaction.CommitAsync(cancellation.Token);
            var result = await pending;
            result.FirstError.ShouldBe(VfsConnectionErrors.LimitReached());
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

        await using var read = CreateConsumerContext();
        (await read.VfsConnections.CountAsync()).ShouldBe(32);
        (await Store(read).AuthenticateAsync(first.Value.Connection.Username, first.Value.Password, default)).ShouldNotBeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RotateAndRevoke_ConcurrentRequests_SerializeWithoutResurrection(bool rotateFirst)
    {
        var original = await SeedCredentialAsync();
        await using var holder = CreateConsumerContext();
        await using var contender = CreateConsumerContext();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var transaction = await new EfUnitOfWork(holder).BeginTransactionAsync(cancellation.Token);
        IssuedVfsCredential? rotated = null;
        if (rotateFirst)
        {
            rotated = (await Store(holder).RotateAsync(userId, original.Connection.Id, cancellation.Token)).Value;
        }
        else
        {
            (await Store(holder).RevokeAsync(userId, original.Connection.Id, cancellation.Token)).IsError.ShouldBeFalse();
        }
        var (pending, waited) = await database.StartWaitingForALockAsync(contender,
            token => HandleAsync(rotateFirst ? "revoke" : "rotate", contender, Transaction(contender), original.Connection.Id, token),
            cancellation.Token);
        Exception? failure = null;
        try
        {
            waited.ShouldBeTrue();
            await transaction.CommitAsync(cancellation.Token);
            var result = await pending;
            if (rotateFirst)
                result.IsError.ShouldBeFalse();
            else
                result.FirstError.ShouldBe(VfsConnectionErrors.Revoked());
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

        await using var read = CreateConsumerContext();
        (await read.VfsConnections.SingleAsync()).RevokedAt.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(Now.ToUnixTimeMilliseconds()));
        (await Store(read).AuthenticateAsync(original.Connection.Username, original.Password, default)).ShouldBeNull();
        if (rotated is not null)
        {
            (await Store(read).AuthenticateAsync(rotated.Connection.Username, rotated.Password, default)).ShouldBeNull();
        }
    }

    [Fact]
    public async Task Create_CancelledWhileWaitingForUserLock_LeavesNoPartialCredentialAndReleasesTransaction()
    {
        await using var holder = CreateConsumerContext();
        await using var contender = CreateConsumerContext();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var transaction = await new EfUnitOfWork(holder).BeginTransactionAsync(cancellation.Token);
        var first = (await Store(holder).CreateAsync(userId, "First", cancellation.Token)).Value;
        var (pending, waited) = await database.StartWaitingForALockAsync(contender,
            token => new CreateVfsConnectionCommandHandler(CurrentUser(), Store(contender), Transaction(contender))
                .HandleAsync(new("Cancelled"), token), cancellation.Token);
        Exception? failure = null;
        try
        {
            waited.ShouldBeTrue();
            await cancellation.CancelAsync();
            var error = await Record.ExceptionAsync(() => pending);
            error.ShouldBeAssignableTo<OperationCanceledException>().CancellationToken.ShouldBe(cancellation.Token);
            contender.Database.CurrentTransaction.ShouldBeNull();
            contender.ChangeTracker.Entries().ShouldBeEmpty();
            await transaction.CommitAsync();
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

        await using var read = CreateConsumerContext();
        (await read.VfsConnections.SingleAsync()).Id.ShouldBe(first.Connection.Id);
        var replacement = await new CreateVfsConnectionCommandHandler(CurrentUser(), Store(read), Transaction(read))
            .HandleAsync(new("After cancellation"));
        replacement.IsError.ShouldBeFalse();
    }

    [Fact]
    public async Task Rotate_InvalidatedCredentialAtActiveLimit_DoesNotReactivateOrChangeVerifier()
    {
        var original = await SeedCredentialAsync();
        await using (var seed = database.CreateContext())
        {
            await seed.Users.Where(user => user.Id == userId).ExecuteUpdateAsync(setters => setters.SetProperty(user => user.SecurityStamp, "new-stamp"));
        }
        await SeedActiveConnectionsAsync(32, "new-stamp");
        await using var db = CreateConsumerContext();
        var before = await db.VfsConnections.SingleAsync(item => item.Id == original.Connection.Id);

        var result = await new RotateVfsConnectionCommandHandler(CurrentUser(), Store(db), Transaction(db))
            .HandleAsync(new(original.Connection.Id));

        result.FirstError.ShouldBe(VfsConnectionErrors.LimitReached());
        var after = await db.VfsConnections.SingleAsync(item => item.Id == original.Connection.Id);
        after.PasswordVerifier.ShouldBe(before.PasswordVerifier);
        after.AccountStamp.ShouldBe("current-stamp");
        (await Store(db).AuthenticateAsync(original.Connection.Username, original.Password, default)).ShouldBeNull();
    }

    [Fact]
    public async Task Revoke_RepeatedRequest_PreservesFirstRevocationInstant()
    {
        var original = await SeedCredentialAsync();
        await using var db = CreateConsumerContext();
        var first = new RevokeVfsConnectionCommandHandler(CurrentUser(), Store(db), Transaction(db));
        (await first.HandleAsync(new(original.Connection.Id))).IsError.ShouldBeFalse();
        var later = new VfsConnectionStore(db, new FixedClock(Now.AddDays(1)));

        (await new RevokeVfsConnectionCommandHandler(CurrentUser(), later, Transaction(db))
            .HandleAsync(new(original.Connection.Id))).IsError.ShouldBeFalse();

        (await db.VfsConnections.SingleAsync()).RevokedAt.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(Now.ToUnixTimeMilliseconds()));
    }

    private RomdDbContext CreateConsumerContext() => new(database.CreateOptions(options =>
        options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking).AddInterceptors(new ConsumerWriteGuardInterceptor())));

    private ICurrentUser CurrentUser()
    {
        var current = Substitute.For<ICurrentUser>();
        current.IsAuthenticated.Returns(true);
        current.UserId.Returns(userId);
        return current;
    }

    private static VfsConnectionStore Store(RomdDbContext db) => new(db, new FixedClock(Now));
    private static ConsumerSecurityTransaction Transaction(RomdDbContext db) =>
        new(new EfUnitOfWork(db), NullLogger<ConsumerSecurityTransaction>.Instance);

    private async Task<IssuedVfsCredential> SeedCredentialAsync()
    {
        await using var db = CreateConsumerContext();
        return (await new CreateVfsConnectionCommandHandler(CurrentUser(), Store(db), Transaction(db))
            .HandleAsync(new("Existing"))).Value;
    }

    private async Task SeedActiveConnectionsAsync(int count, string stamp = "current-stamp")
    {
        await using var db = database.CreateContext();
        db.VfsConnections.AddRange(Enumerable.Range(0, count).Select(index => new VfsConnectionEntity
        {
            UserId = userId,
            Name = $"Existing {index}",
            AccountStamp = stamp,
            PasswordVerifier = new byte[32],
            CreatedAt = Now,
            CredentialUpdatedAt = Now
        }));
        await db.SaveChangesAsync();
    }

    private async Task<ErrorOr<object>> HandleAsync(string operation, RomdDbContext db, IConsumerSecurityTransaction transaction,
        Guid id, CancellationToken ct) => operation switch
        {
            "create" => Box(await new CreateVfsConnectionCommandHandler(CurrentUser(), Store(db), transaction).HandleAsync(new("New"), ct)),
            "rotate" => Box(await new RotateVfsConnectionCommandHandler(CurrentUser(), Store(db), transaction).HandleAsync(new(id), ct)),
            "revoke" => Box(await new RevokeVfsConnectionCommandHandler(CurrentUser(), Store(db), transaction).HandleAsync(new(id), ct)),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    private static ErrorOr<object> Box<T>(ErrorOr<T> result)
    {
        if (result.IsError)
            return result.Errors;
        return (object)result.Value!;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RejectAfterFlush(RomdDbContext db, IConsumerSecurityTransaction inner, string failure,
        CancellationTokenSource cancellation) : IConsumerSecurityTransaction
    {
        public Exception Fault { get; } = new InvalidOperationException("injected post-flush failure");
        public IssuedVfsCredential? RejectedCredential { get; private set; }
        public bool Flushed { get; private set; }

        public Task<ErrorOr<T>> ExecuteAsync<T>(Func<CancellationToken, Task<ErrorOr<T>>> mutation, CancellationToken ct = default) =>
            inner.ExecuteAsync<T>(async token =>
            {
                var result = await mutation(token);
                result.IsError.ShouldBeFalse();
                RejectedCredential = result.Value as IssuedVfsCredential;
                await new EfUnitOfWork(db).FlushAsync(token);
                Flushed = true;
                if (failure == "error")
                    return VfsConnectionErrors.LimitReached();
                if (failure == "fault")
                    throw Fault;
                await cancellation.CancelAsync();
                return result;
            }, ct);
    }

    private sealed class CleanupFaultUnitOfWork(IUnitOfWork inner) : IUnitOfWork
    {
        public Task FlushAsync(CancellationToken cancellationToken = default) => inner.FlushAsync(cancellationToken);
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new CleanupFaultTransaction(await inner.BeginTransactionAsync(cancellationToken));

        private sealed class CleanupFaultTransaction(ITransaction innerTransaction) : ITransaction
        {
            public Task CommitAsync(CancellationToken cancellationToken = default) => innerTransaction.CommitAsync(cancellationToken);
            public Task RollbackAsync(CancellationToken cancellationToken = default) => innerTransaction.RollbackAsync(cancellationToken);
            public async ValueTask DisposeAsync()
            {
                await innerTransaction.DisposeAsync();
                throw new InvalidOperationException("injected cleanup failure after real commit");
            }
        }
    }

    public void Dispose() => database.Dispose();
}
