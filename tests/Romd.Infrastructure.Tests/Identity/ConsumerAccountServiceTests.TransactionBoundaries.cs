using ErrorOr;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Application.Common.Security;
using Romd.Consumer.Application;
using Romd.Consumer.Application.Common.Persistence;
using Romd.Infrastructure.Identity;
using Romd.Infrastructure.Tests.Helpers;
using Romd.Persistence;
using Romd.Persistence.Identity;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Identity;

public sealed partial class ConsumerAccountServiceTests
{
    [Fact]
    public async Task Prepare_VerificationPolicyAndHashing_RunOutsideTransactionWithoutMutatingCredentials()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var user = await SeedUserAsync(db);
        var calls = new List<string>();
        var hasher = CreateHasher();
        hasher.VerifyHashedPassword(Arg.Any<RomdUser>(), "original-hash", "current-password").Returns(_ =>
        {
            db.Database.CurrentTransaction.ShouldBeNull();
            calls.Add("verify");
            return PasswordVerificationResult.Success;
        });
        var policy = new ObservingPasswordPolicy(() =>
        {
            db.Database.CurrentTransaction.ShouldBeNull();
            calls.Add("policy");
        });
        hasher.HashPassword(Arg.Any<RomdUser>(), "new-password").Returns(_ =>
        {
            db.Database.CurrentTransaction.ShouldBeNull();
            calls.Add("hash");
            return "new-hash";
        });
        var sessions = Substitute.For<IAccountSessions>();
        var service = new ConsumerAccountService(db, hasher, policy, sessions);

        var prepared = await service.PrepareAsync(user.Id, "current-password", "new-password");

        prepared.IsError.ShouldBeFalse();
        calls.ShouldBe(["verify", "policy", "hash"]);
        db.Database.CurrentTransaction.ShouldBeNull();
        var persisted = await db.Users.AsNoTracking().SingleAsync();
        persisted.PasswordHash.ShouldBe("original-hash");
        persisted.SecurityStamp.ShouldBe("original-stamp");
        persisted.ConcurrencyStamp.ShouldBe("original-concurrency");
        sessions.ReceivedCalls().ShouldBeEmpty();
        var description = prepared.Value.ToString().ShouldNotBeNull();
        foreach (string secret in new[] { "current-password", "new-password", "original-hash", "new-hash" })
            description.ShouldNotContain(secret);
    }

    [Fact]
    public async Task Prepare_InsideTransaction_RejectsBeforePasswordWork()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var user = await SeedUserAsync(db);
        var hasher = CreateHasher();
        var service = CreateService(db, hasher);
        await using var transaction = await new EfUnitOfWork(db).BeginTransactionAsync();

        var error = await Should.ThrowAsync<InvalidOperationException>(() => service.PrepareAsync(user.Id, "current-password", "new-password"));

        error.Message.ShouldBe("Password preparation must run outside a transaction.");
        hasher.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Apply_WithoutCallerTransaction_RejectsWithoutChangingCredentialsOrSessions()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var user = await SeedUserAsync(db);
        var sessions = new AccountSessionStore(db, TimeProvider.System);
        await sessions.StartCommittedAsync(db, TimeProvider.System, user.Id, "client", "Current device", user.SecurityStamp!, default);
        var prepared = (await CreateService(db, sessions: sessions).PrepareAsync(user.Id, "current-password", "new-password")).Value;

        var error = await Should.ThrowAsync<InvalidOperationException>(() => prepared.ApplyStagedAsync());

        error.Message.ShouldBe("Password changes require a caller-owned transaction.");
        await AssertOriginalAsync(database);
    }

    [Fact]
    public async Task Apply_CallerRollsBack_UndoesCredentialsAndEverySessionRevocation()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var user = await SeedUserAsync(db);
        var sessions = new AccountSessionStore(db, TimeProvider.System);
        await sessions.StartCommittedAsync(db, TimeProvider.System, user.Id, "client", "Current device", user.SecurityStamp!, default);
        await sessions.StartCommittedAsync(db, TimeProvider.System, user.Id, "other-client", "Other device", user.SecurityStamp!, default);
        var prepared = (await CreateService(db, sessions: sessions).PrepareAsync(user.Id, "current-password", "new-password")).Value;
        await using var transaction = await new EfUnitOfWork(db).BeginTransactionAsync();

        (await prepared.ApplyStagedAsync()).Value.ShouldBe(Result.Updated);

        (await db.Users.AsNoTracking().SingleAsync()).PasswordHash.ShouldBe("new-hash");
        (await db.AccountSessions.CountAsync(item => item.RevokedAt != null)).ShouldBe(2);
        await AssertOriginalAsync(database, expectedSessions: 2);
        await transaction.RollbackAsync();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await AssertOriginalAsync(database, expectedSessions: 2);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("fault")]
    [InlineData("cancel")]
    public async Task ChangePassword_PostFlushFailure_RollsBackCredentialsAndSessions(string failure)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var user = await SeedUserAsync(db);
        var sessions = new AccountSessionStore(db, TimeProvider.System);
        await sessions.StartCommittedAsync(db, TimeProvider.System, user.Id, "client", "Current device", user.SecurityStamp!, default);
        await sessions.StartCommittedAsync(db, TimeProvider.System, user.Id, "other-client", "Other device", user.SecurityStamp!, default);
        using var cancellation = new CancellationTokenSource();
        var transaction = new RejectPasswordAfterFlush(db, Transaction(db), failure, cancellation);

        if (failure == "error")
        {
            var result = await ChangePasswordAsync(db, CreateService(db, sessions: sessions), user.Id,
                "current-password", "new-password", cancellation.Token, transaction);
            result.FirstError.ShouldBe(ConsumerErrors.PasswordChangeFailed("Injected post-flush rejection."));
        }
        else
        {
            var error = await CapturePasswordFailureAsync(() => ChangePasswordAsync(db, CreateService(db, sessions: sessions), user.Id,
                "current-password", "new-password", cancellation.Token, transaction));
            if (failure == "fault")
                error.ShouldBeSameAs(transaction.Fault);
            else
                error.ShouldBeAssignableTo<OperationCanceledException>().ShouldNotBeNull().CancellationToken.ShouldBe(cancellation.Token);
        }

        transaction.Flushed.ShouldBeTrue();
        db.Database.CurrentTransaction.ShouldBeNull();
        db.ChangeTracker.Entries().ShouldBeEmpty();
        await AssertOriginalAsync(database, expectedSessions: 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangePassword_TransactionFailure_ReportsWhetherCredentialsAndSessionsAreDurable(bool cleanupAfterCommit)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var user = await SeedUserAsync(db);
        var sessions = new AccountSessionStore(db, TimeProvider.System);
        await sessions.StartCommittedAsync(db, TimeProvider.System, user.Id, "client", "Current device", user.SecurityStamp!, default);
        var unit = new FaultingPasswordUnitOfWork(new EfUnitOfWork(db), cleanupAfterCommit);
        var transaction = new ConsumerSecurityTransaction(unit, NullLogger<ConsumerSecurityTransaction>.Instance);

        if (cleanupAfterCommit)
        {
            var result = await ChangePasswordAsync(db, CreateService(db, sessions: sessions), user.Id,
                "current-password", "new-password", transaction: transaction);
            result.Value.ShouldBe(Result.Updated);
            await using var read = database.CreateContext();
            var persisted = await read.Users.SingleAsync();
            persisted.PasswordHash.ShouldBe("new-hash");
            persisted.SecurityStamp.ShouldNotBe(user.SecurityStamp);
            persisted.ConcurrencyStamp.ShouldNotBe(user.ConcurrencyStamp);
            var session = await read.AccountSessions.SingleAsync();
            session.RevokedAt.ShouldNotBeNull();
            session.RevokedBy.ShouldBe(user.Id);
            session.RevocationReason.ShouldBe("Password changed");
        }
        else
        {
            var error = await CapturePasswordFailureAsync(() => ChangePasswordAsync(db, CreateService(db, sessions: sessions), user.Id,
                "current-password", "new-password", transaction: transaction));
            error.ShouldBeSameAs(unit.Fault);
            await AssertOriginalAsync(database);
        }
        db.Database.CurrentTransaction.ShouldBeNull();
    }

    [Fact]
    public async Task ChangePassword_Success_RevokesCurrentAndOtherSessionsAndInvalidatesVfsCredential()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        var user = await SeedUserAsync(db);
        var sessions = new AccountSessionStore(db, TimeProvider.System);
        await sessions.StartCommittedAsync(db, TimeProvider.System, user.Id, "client", "Current device", user.SecurityStamp!, default);
        await sessions.StartCommittedAsync(db, TimeProvider.System, user.Id, "other-client", "Other device", user.SecurityStamp!, default);
        var connections = new VfsConnectionStore(db, TimeProvider.System);
        var credential = (await Transaction(db).ExecuteAsync(token => connections.CreateAsync(user.Id, "Deck", token))).Value;

        var result = await ChangePasswordAsync(db, CreateService(db, sessions: sessions), user.Id, "current-password", "new-password");

        result.Value.ShouldBe(Result.Updated);
        await using var read = database.CreateContext();
        (await read.AccountSessions.CountAsync()).ShouldBe(2);
        (await read.AccountSessions.CountAsync(item => item.RevokedAt != null && item.RevokedBy == user.Id && item.RevocationReason == "Password changed")).ShouldBe(2);
        var vfs = new VfsConnectionStore(read, TimeProvider.System);
        (await vfs.AuthenticateAsync(credential.Connection.Username, credential.Password, default)).ShouldBeNull();
        (await vfs.ListAsync(user.Id, default)).ShouldHaveSingleItem().Status.ShouldBe("Invalidated");
        (await read.VfsConnections.SingleAsync()).RevokedAt.ShouldBeNull();
    }

    [Fact]
    public async Task ChangePassword_ConcurrentPreparedChanges_OnlyFirstCommitsAndSecondPreservesWinner()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var holder = database.CreateContext();
        var user = await SeedUserAsync(holder);
        var firstHasher = CreateHasher();
        firstHasher.HashPassword(Arg.Any<RomdUser>(), "new-password").Returns("winner-hash");
        var first = (await CreateService(holder, firstHasher).PrepareAsync(user.Id, "current-password", "new-password")).Value;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var transaction = await new EfUnitOfWork(holder).BeginTransactionAsync(cancellation.Token);
        (await first.ApplyStagedAsync(cancellation.Token)).Value.ShouldBe(Result.Updated);
        await using var contender = database.CreateContext();
        var (pending, waited) = await database.StartWaitingForALockAsync(contender,
            token => ChangePasswordAsync(contender, CreateService(contender), user.Id, "current-password", "new-password", token),
            cancellation.Token);
        Exception? failure = null;
        try
        {
            waited.ShouldBeTrue();
            await transaction.CommitAsync(cancellation.Token);
            var result = await pending;
            result.FirstError.ShouldBe(ConsumerErrors.PasswordChangeFailed("The account changed while updating the password. Try again."));
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

        await using var read = database.CreateContext();
        var persisted = await read.Users.SingleAsync();
        persisted.PasswordHash.ShouldBe("winner-hash");
        persisted.SecurityStamp.ShouldNotBe("original-stamp");
        persisted.ConcurrencyStamp.ShouldNotBe("original-concurrency");
    }

    private static ConsumerSecurityTransaction Transaction(RomdDbContext db) =>
        new(new EfUnitOfWork(db), NullLogger<ConsumerSecurityTransaction>.Instance);

    private static async Task AssertOriginalAsync(PostgreSqlTestDatabase database, int expectedSessions = 1)
    {
        await using var read = database.CreateContext();
        var persisted = await read.Users.SingleAsync();
        persisted.PasswordHash.ShouldBe("original-hash");
        persisted.SecurityStamp.ShouldBe("original-stamp");
        persisted.ConcurrencyStamp.ShouldBe("original-concurrency");
        var sessions = await read.AccountSessions.ToListAsync();
        sessions.Count.ShouldBe(expectedSessions);
        sessions.ShouldAllBe(session => session.RevokedAt == null);
    }

    private static async Task<Exception?> CapturePasswordFailureAsync(Func<Task> operation)
    {
        try
        {
            await operation();
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    private sealed class ObservingPasswordPolicy(Action observe) : IConsumerPasswordPolicy
    {
        public IdentityResult Validate(RomdUser user, string password)
        {
            observe();
            return IdentityResult.Success;
        }
    }

    private sealed class RejectPasswordAfterFlush(RomdDbContext db, IConsumerSecurityTransaction inner, string failure,
        CancellationTokenSource cancellation) : IConsumerSecurityTransaction
    {
        public Exception Fault { get; } = new IOException("injected post-flush failure");
        public bool Flushed { get; private set; }
        public Task<ErrorOr<T>> ExecuteAsync<T>(Func<CancellationToken, Task<ErrorOr<T>>> mutation, CancellationToken ct = default) =>
            inner.ExecuteAsync<T>(async token =>
            {
                var result = await mutation(token);
                result.IsError.ShouldBeFalse();
                await new EfUnitOfWork(db).FlushAsync(token);
                (await db.Users.AsNoTracking().SingleAsync(token)).PasswordHash.ShouldBe("new-hash");
                (await db.AccountSessions.CountAsync(item => item.RevokedAt != null, token)).ShouldBe(2);
                Flushed = true;
                if (failure == "error")
                    return ConsumerErrors.PasswordChangeFailed("Injected post-flush rejection.");
                if (failure == "fault")
                    throw Fault;
                await cancellation.CancelAsync();
                return result;
            }, ct);
    }

    private sealed class FaultingPasswordUnitOfWork(IUnitOfWork inner, bool cleanupAfterCommit) : IUnitOfWork
    {
        private bool CleanupAfterCommit => cleanupAfterCommit;
        public Exception Fault { get; } = new IOException("injected transaction failure");
        public Task FlushAsync(CancellationToken cancellationToken = default) => inner.FlushAsync(cancellationToken);
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new FaultingTransaction(await inner.BeginTransactionAsync(cancellationToken), this);

        private sealed class FaultingTransaction(ITransaction transaction, FaultingPasswordUnitOfWork owner) : ITransaction
        {
            public async Task CommitAsync(CancellationToken cancellationToken = default)
            {
                if (!owner.CleanupAfterCommit)
                {
                    await owner.FlushAsync(cancellationToken);
                    throw owner.Fault;
                }
                await transaction.CommitAsync(cancellationToken);
            }
            public Task RollbackAsync(CancellationToken cancellationToken = default) => transaction.RollbackAsync(cancellationToken);
            public async ValueTask DisposeAsync()
            {
                await transaction.DisposeAsync();
                if (owner.CleanupAfterCommit)
                    throw owner.Fault;
            }
        }
    }
}
