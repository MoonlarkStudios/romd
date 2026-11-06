using ErrorOr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Romd.Admin.Application.Common.Persistence;
using Romd.Domain.Hashing;
using Romd.Persistence;
using Romd.Persistence.Entities;
using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class UnitOfWorkFailureRecoveryTests
{
    [Theory]
    [InlineData("mutation", false)]
    [InlineData("mutation", true)]
    [InlineData("commit", false)]
    [InlineData("commit", true)]
    [InlineData("acknowledgement", false)]
    [InlineData("acknowledgement", true)]
    public async Task ExecuteInTransactionAsync_PrimaryAndDisposalFail_PreservesCauseAndDatabaseOutcome(string phase, bool cancel)
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        using var cancellation = new CancellationTokenSource();
        Exception primary = cancel
            ? new OperationCanceledException("Primary transaction interruption", cancellation.Token)
            : new IOException("Primary transaction interruption");
        var work = new InterruptedWork(new EfUnitOfWork(db), phase, primary, cancellation, cancel);
        var logger = Substitute.For<ILogger>();

        var actual = await Record.ExceptionAsync(() => work.ExecuteInTransactionAsync<Success>(async token =>
        {
            db.Files.Add(NewFile());
            await work.FlushAsync(token);
            if (phase == "mutation")
            {
                if (cancel)
                    await cancellation.CancelAsync();
                throw primary;
            }
            return Result.Success;
        }, logger, cancellation.Token));

        actual.ShouldBeSameAs(primary);
        if (cancel)
            ((OperationCanceledException)actual!).CancellationToken.ShouldBe(cancellation.Token);
        work.Disposals.ShouldBe(1);
        logger.ReceivedCalls().ShouldContain(call => call.GetMethodInfo().Name == "Log" &&
            ReferenceEquals(call.GetArguments()[3], work.CleanupFailure));
        db.Database.CurrentTransaction.ShouldBeNull();
        await using var verify = database.CreateContext();
        (await verify.Files.CountAsync()).ShouldBe(phase == "acknowledgement" ? 1 : 0);
        if (phase != "acknowledgement")
        {
            db.ChangeTracker.Entries().ShouldBeEmpty();
            await db.SaveChangesAsync();
            (await verify.Files.CountAsync()).ShouldBe(0);
        }
    }

    [Fact]
    public async Task ExecuteInTransactionAsync_AcknowledgedCommitAndDisposalFail_ReturnsDurableSuccess()
    {
        await using var database = PostgreSqlTestDatabase.Create();
        await using var db = database.CreateContext();
        using var cancellation = new CancellationTokenSource();
        var work = new InterruptedWork(new EfUnitOfWork(db), "none", new IOException(), cancellation, false);
        var logger = Substitute.For<ILogger>();

        var result = await work.ExecuteInTransactionAsync<int>(_ =>
        {
            db.Files.Add(NewFile());
            return Task.FromResult<ErrorOr<int>>(17);
        }, logger);

        result.Value.ShouldBe(17);
        work.Disposals.ShouldBe(1);
        logger.ReceivedCalls().ShouldContain(call => call.GetMethodInfo().Name == "Log" &&
            ReferenceEquals(call.GetArguments()[3], work.CleanupFailure));
        await using var verify = database.CreateContext();
        (await verify.Files.CountAsync()).ShouldBe(1);
    }

    private static FileEntityPersistence NewFile() => new()
    {
        Sha256 = Sha256.Parse(new string('1', 64)),
        Size = 1,
        SizeOnDisk = 1,
        CreatedAt = DateTimeOffset.UnixEpoch
    };

    private sealed class InterruptedWork(IUnitOfWork inner, string phase, Exception primary,
        CancellationTokenSource cancellation, bool cancel) : IUnitOfWork
    {
        public IOException CleanupFailure { get; } = new("Secondary transaction cleanup failure");
        public int Disposals { get; private set; }
        public Task FlushAsync(CancellationToken cancellationToken = default) => inner.FlushAsync(cancellationToken);
        public TransactionConflict ConflictOf(Exception error) => inner.ConflictOf(error);
        public async Task<ITransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new Transaction(this, await inner.BeginTransactionAsync(cancellationToken));

        private async Task CommitAsync(ITransaction transaction, CancellationToken cancellationToken)
        {
            if (phase == "commit")
                await inner.FlushAsync(cancellationToken);
            else
                await transaction.CommitAsync(cancellationToken);
            if (phase is "commit" or "acknowledgement")
            {
                if (cancel)
                    await cancellation.CancelAsync();
                throw primary;
            }
        }

        private sealed class Transaction(InterruptedWork owner, ITransaction transaction) : ITransaction
        {
            public Task CommitAsync(CancellationToken cancellationToken = default) => owner.CommitAsync(transaction, cancellationToken);
            public Task RollbackAsync(CancellationToken cancellationToken = default) => transaction.RollbackAsync(cancellationToken);
            public async ValueTask DisposeAsync()
            {
                owner.Disposals++;
                await transaction.DisposeAsync();
                throw owner.CleanupFailure;
            }
        }
    }
}
