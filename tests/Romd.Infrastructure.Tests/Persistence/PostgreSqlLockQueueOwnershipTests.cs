using Romd.PostgreSql.TestSupport;
using Shouldly;
using Xunit;

namespace Romd.Infrastructure.Tests.Persistence;

public sealed class PostgreSqlLockQueueOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartAndObserve_ObserverFails_CancelsAndJoinsTheWriteBeforePropagating(bool cancellation)
    {
        using var caller = new CancellationTokenSource();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception failure = cancellation ? new OperationCanceledException("Observer cancelled.", caller.Token)
            : new InvalidOperationException("Observer query failed.");
        bool cleanupFinished = false;
        Task<int>? write = null;
        async Task<int> WriteAsync(CancellationToken ct)
        {
            try
            {
                await releaseWrite.Task.WaitAsync(ct);
                return 7;
            }
            finally
            {
                cancelled.TrySetResult();
                await releaseCleanup.Task;
                cleanupFinished = true;
            }
        }
        var owner = PostgreSqlLockQueue.StartAndObserveAsync(ct => write = WriteAsync(ct),
            (_, _) => Task.FromException<bool>(failure), caller.Token);
        try
        {
            await Task.WhenAny(owner, cancelled.Task).WaitAsync(TimeSpan.FromSeconds(5));
            cancelled.Task.IsCompleted.ShouldBeTrue();
            owner.IsCompleted.ShouldBeFalse("the write still owns resources until its asynchronous cleanup finishes");

            releaseCleanup.SetResult();
            var observed = await Record.ExceptionAsync(() => owner);

            observed.ShouldBeSameAs(failure);
            cleanupFinished.ShouldBeTrue();
            write.ShouldNotBeNull().IsCompleted.ShouldBeTrue();
        }
        finally
        {
            await caller.CancelAsync();
            releaseWrite.TrySetResult();
            releaseCleanup.TrySetResult();
            if (write is not null)
                await Record.ExceptionAsync(() => write);
            await Record.ExceptionAsync(() => owner);
        }
    }

    [Fact]
    public async Task StartAndObserve_WriteCleanupAlsoFails_PreservesTheObserverFailureAfterJoining()
    {
        using var caller = new CancellationTokenSource();
        var failure = new InvalidOperationException("Observer query failed.");
        bool cleanupFinished = false;
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int>? write = null;
        async Task<int> WriteAsync(CancellationToken ct)
        {
            using var registration = ct.Register(() =>
            {
                cancelled.TrySetResult();
                throw new IOException("Cancellation callback failed.");
            });
            try
            {
                await cancelled.Task;
                return 7;
            }
            finally
            {
                cleanupFinished = true;
                throw new IOException("Write cleanup failed.");
            }
        }
        try
        {
            var observed = await Record.ExceptionAsync(() => PostgreSqlLockQueue.StartAndObserveAsync(
                ct => write = WriteAsync(ct), (_, _) => Task.FromException<bool>(failure), caller.Token));

            var aggregate = observed.ShouldBeOfType<AggregateException>().Flatten();
            aggregate.InnerExceptions.ShouldContain(error => ReferenceEquals(error, failure));
            aggregate.InnerExceptions.ShouldContain(error => error.Message == "Cancellation callback failed.");
            aggregate.InnerExceptions.ShouldContain(error => error.Message == "Write cleanup failed.");
            cleanupFinished.ShouldBeTrue();
            write.ShouldNotBeNull().IsCompleted.ShouldBeTrue();
        }
        finally
        {
            await Record.ExceptionAsync(() => caller.CancelAsync());
            cancelled.TrySetResult();
            if (write is not null)
                await Record.ExceptionAsync(() => write);
        }
    }

    [Fact]
    public async Task StartAndObserve_ObserverReturnsWhileWriteWaits_CallerCancellationStillReachesTheWrite()
    {
        using var caller = new CancellationTokenSource();
        bool cleanupFinished = false;
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<int> WriteAsync(CancellationToken ct)
        {
            try
            {
                await releaseWrite.Task.WaitAsync(ct);
                return 7;
            }
            finally { cleanupFinished = true; }
        }
        var (pending, waited) = await PostgreSqlLockQueue.StartAndObserveAsync(WriteAsync,
            (_, _) => Task.FromResult(true), caller.Token);

        try
        {
            waited.ShouldBeTrue();
            pending.IsCompleted.ShouldBeFalse();
            await caller.CancelAsync();
            var cancellation = await Should.ThrowAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            cancellation.CancellationToken.ShouldBe(caller.Token);
            cleanupFinished.ShouldBeTrue();
            await PostgreSqlLockQueue.CancelAndJoinAsync(caller, null, pending);
        }
        finally
        {
            await caller.CancelAsync();
            releaseWrite.TrySetResult();
            await Record.ExceptionAsync(() => pending);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartAndObserve_WriteFailsBeforeQueuing_PreservesTheOriginalFailure(bool synchronous)
    {
        var failure = new InvalidOperationException("Writer failed.");
        Task<int> Write(CancellationToken _) => synchronous ? throw failure : Task.FromException<int>(failure);
        var observed = await Record.ExceptionAsync(() => PostgreSqlLockQueue.StartAndObserveAsync(Write,
            async (pending, _) => { await pending; return false; }, CancellationToken.None));

        observed.ShouldBeSameAs(failure);
    }

    [Fact]
    public async Task StartAndObserve_WriteFinishesWithoutWaiting_ReturnsItsResult()
    {
        var (pending, waited) = await PostgreSqlLockQueue.StartAndObserveAsync(_ => Task.FromResult(7),
            async (write, _) => { await write; return false; }, CancellationToken.None);

        waited.ShouldBeFalse();
        (await pending).ShouldBe(7);
    }

    [Fact]
    public async Task CancelAndJoin_CallerOwnedOperations_JoinsEveryWriteAndKeepsTheSourceOwnedByTheCaller()
    {
        using var caller = new CancellationTokenSource();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task WriteAsync()
        {
            try
            { await release.Task.WaitAsync(caller.Token); }
            finally
            {
                cleanupEntered.TrySetResult();
                await release.Task;
            }
        }
        var first = WriteAsync();
        var second = WriteAsync();
        var cleanup = PostgreSqlLockQueue.CancelAndJoinAsync(caller, null, first, second);
        try
        {
            await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cleanup.IsCompleted.ShouldBeFalse();
            release.SetResult();
            await cleanup;

            first.IsCompleted.ShouldBeTrue();
            second.IsCompleted.ShouldBeTrue();
            caller.Token.IsCancellationRequested.ShouldBeTrue();
        }
        finally
        {
            release.TrySetResult();
            await cleanup;
        }
    }

    [Fact]
    public async Task CancelAndJoin_NoPrimaryFailure_ReportsCallbackFailureAfterJoiningEveryWrite()
    {
        using var caller = new CancellationTokenSource();
        var failure = new IOException("Cancellation callback failed.");
        using var registration = caller.Token.Register(() => throw failure);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = PostgreSqlLockQueue.CancelAndJoinAsync(caller, null, release.Task);
        try
        {
            cleanup.IsCompleted.ShouldBeFalse();
            release.SetResult();

            var observed = await Record.ExceptionAsync(() => cleanup);

            observed.ShouldBeSameAs(failure);
        }
        finally
        {
            release.TrySetResult();
            await Record.ExceptionAsync(() => cleanup);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelAndJoin_NoPrimaryFailure_ReportsWriteFailureAfterJoiningLaterWrites(bool unrequestedCancellation)
    {
        using var caller = new CancellationTokenSource();
        Exception failure = unrequestedCancellation ? new OperationCanceledException("Unrequested cancellation.")
            : new IOException("Write failed.");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = PostgreSqlLockQueue.CancelAndJoinAsync(caller, null, Task.FromException(failure), release.Task);
        try
        {
            cleanup.IsCompleted.ShouldBeFalse();
            release.SetResult();

            var observed = await Record.ExceptionAsync(() => cleanup);

            observed.ShouldBeSameAs(failure);
        }
        finally
        {
            release.TrySetResult();
            await Record.ExceptionAsync(() => cleanup);
        }
    }

    [Fact]
    public async Task CancelAndJoin_NoPrimaryFailure_ReportsAnUnrelatedRequestedCancellation()
    {
        using var caller = new CancellationTokenSource();
        var failure = new OperationCanceledException("Unrelated requested cancellation.", new CancellationToken(true));

        var observed = await Record.ExceptionAsync(() => PostgreSqlLockQueue.CancelAndJoinAsync(caller, null, Task.FromException(failure)));

        caller.IsCancellationRequested.ShouldBeTrue();
        observed.ShouldBeSameAs(failure);
    }

    [Fact]
    public async Task StartAndObserve_ReturnedWriteFailsWithAnotherToken_CleanupDoesNotNormalizeOrIgnoreIt()
    {
        using var caller = new CancellationTokenSource();
        var write = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new OperationCanceledException("Unrelated requested cancellation.", new CancellationToken(true));
        var (pending, _) = await PostgreSqlLockQueue.StartAndObserveAsync(_ => write.Task,
            (_, _) => Task.FromResult(true), caller.Token);

        try
        {
            await caller.CancelAsync();
            write.SetException(failure);

            var observed = await Record.ExceptionAsync(() => pending);
            var cleanup = await Record.ExceptionAsync(() => PostgreSqlLockQueue.CancelAndJoinAsync(caller, null, pending));

            observed.ShouldBeSameAs(failure);
            cleanup.ShouldBeSameAs(failure);
        }
        finally
        {
            write.TrySetResult(7);
            await Record.ExceptionAsync(() => pending);
        }
    }
}
